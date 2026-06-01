using FileIt.Domain.Entities;
using FileIt.Domain.Entities.Api;
using FileIt.Domain.Interfaces;
using FileIt.Module.SimpleFlow.App;
using FileIt.Module.SimpleFlow.App.WaitOnApiUpload;
using Microsoft.Extensions.Logging;
using Moq;

namespace FileIt.Module.SimpleFlow.Test.WaitOnApiUpload;

[TestClass]
public class TestBasicApiAddHandler
{
    public required Mock<ILogger<BasicApiAddHandler>> _loggerMock;
    public required Mock<IHandleFiles> _blobToolMock;
    public required Mock<ISimpleRequestLogRepo> _requestLogRepoMock;
    public required SimpleConfig _config;
    public required BasicApiAddHandler target;

    [TestInitialize]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<BasicApiAddHandler>>();
        _loggerMock.Setup(m =>
            m.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()
            )
        );
        _blobToolMock = new Mock<IHandleFiles>();
        _requestLogRepoMock = new Mock<ISimpleRequestLogRepo>();

        _config = new SimpleConfig()
        {
            SourceContainer = "simple-source",
            WorkingContainer = "simple-working",
            FinalContainer = "simple-final",
            ApiAddQueueName = "api-add",
            ApiAddTopicName = "api-add-topic",
        };

        target = new BasicApiAddHandler(
            _loggerMock.Object,
            _blobToolMock.Object,
            _requestLogRepoMock.Object,
            _config
        );
    }

    private static ApiAddResponse BuildMessage(string? correlationId, int nodeId = 99)
    {
        return new ApiAddResponse()
        {
            NodeId = nodeId,
            CorrelationId = correlationId,
            TopicName = "api-add-topic",
        };
    }

    [TestMethod]
    public async Task RunAsync_HappyPath_MovesBlobAndStampsApiId()
    {
        var correlationId = Guid.NewGuid().ToString();
        var entry = new SimpleRequestLog { Id = 7, BlobName = "good.txt", ClientRequestId = correlationId };
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(correlationId))
            .ReturnsAsync(entry);

        await target.RunAsync(BuildMessage(correlationId, nodeId: 555));

        _blobToolMock.Verify(x => x.MoveAsync(
            "good.txt", "simple-working", "simple-final", It.IsAny<CancellationToken>()
        ), Times.Once);
        _requestLogRepoMock.Verify(x => x.UpdateAsync(
            It.Is<SimpleRequestLog>(e => e.Id == 7 && e.ApiId == 555)
        ), Times.Once);
    }

    [TestMethod]
    public async Task RunAsync_RequestLogMissing_SkipsBenignly()
    {
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(It.IsAny<string>()))
            .ReturnsAsync((SimpleRequestLog?)null);

        await target.RunAsync(BuildMessage(Guid.NewGuid().ToString()));

        _blobToolMock.Verify(x => x.MoveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()
        ), Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_BlobNameMissing_Throws()
    {
        var correlationId = Guid.NewGuid().ToString();
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(correlationId))
            .ReturnsAsync(new SimpleRequestLog { Id = 7, BlobName = "", ClientRequestId = correlationId });

        await Assert.ThrowsAsync<Exception>(
            () => target.RunAsync(BuildMessage(correlationId)));
    }

    [TestMethod]
    public async Task RunAsync_RequestLogMissing_DoesNotMoveBlob()
    {
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(It.IsAny<string>()))
            .ReturnsAsync((SimpleRequestLog?)null);

        try { await target.RunAsync(BuildMessage(Guid.NewGuid().ToString())); }
        catch (Exception) { }

        _blobToolMock.Verify(x => x.MoveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()
        ), Times.Never);
        _requestLogRepoMock.Verify(x => x.UpdateAsync(
            It.IsAny<SimpleRequestLog>()
        ), Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_CancellationRequested_ThrowsBeforeMovingBlob()
    {
        var correlationId = Guid.NewGuid().ToString();
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(correlationId))
            .ReturnsAsync(new SimpleRequestLog { Id = 7, BlobName = "good.txt", ClientRequestId = correlationId });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => target.RunAsync(BuildMessage(correlationId), cts.Token));

        _blobToolMock.Verify(x => x.MoveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()
        ), Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_NullCorrelationId_LooksUpWithEmptyString()
    {
        _requestLogRepoMock
            .Setup(x => x.GetByClientRequestIdAsync(string.Empty))
            .ReturnsAsync(new SimpleRequestLog { Id = 1, BlobName = "x.txt", ClientRequestId = string.Empty });

        await target.RunAsync(BuildMessage(correlationId: null));

        _requestLogRepoMock.Verify(x => x.GetByClientRequestIdAsync(string.Empty), Times.Once);
    }
}
