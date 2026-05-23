using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FileIt.Domain.Entities;
using FileIt.Domain.Entities.Api;
using FileIt.Domain.Interfaces;
using FileIt.Module.SimpleFlow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.SimpleFlow.App.WaitOnApiUpload;

public interface IBasicApiAddHandler
{
    Task RunAsync(ApiAddResponse message, CancellationToken cancellationToken = default);
}

public class BasicApiAddHandler : IBasicApiAddHandler
{
    private readonly ILogger<BasicApiAddHandler> _logger;
    private readonly ISimpleRequestLogRepo _requestLogRepo;
    private readonly IHandleFiles _blobTool;
    private readonly SimpleConfig _config;

    public BasicApiAddHandler(
        ILogger<BasicApiAddHandler> logger,
        IHandleFiles blobTool,
        ISimpleRequestLogRepo requestLogRepo,
        SimpleConfig config
    )
    {
        _blobTool = blobTool;
        _requestLogRepo = requestLogRepo;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// A ServiceBusTrigger that processes the file ingested
    /// </summary>
    /// <param name="message">the ServiceBusReceivedMessage</param>
    /// <param name="cancellationToken">token to observe for graceful cancellation</param>
    /// <returns></returns>
    public async Task RunAsync(ApiAddResponse message, CancellationToken cancellationToken = default)
    {
        string clientRequestId = message.CorrelationId ?? string.Empty;

        _logger.LogInformation(
            SimpleEvents.SimpleSubscriberGetRequestLog,
            "Get RequestLog by CorrelationId {CorrelationId}",
            message.CorrelationId
        );
        SimpleRequestLog? entry = await _requestLogRepo.GetByClientRequestIdAsync(clientRequestId);
        if (entry == null)
        {
            // The api-add-topic is a fan-out. A broadcast that originated from the pure
            // API path (services test producer or ApiAddCommand) has no SimpleRequestLog,
            // because no file was ever dropped into simple-source for it. That is not an
            // error for this subscriber; the message simply was not addressed to us.
            // Returning cleanly completes the message so it does not retry and dead-letter.
            _logger.LogInformation(
                SimpleEvents.SimpleSubscriberRequestLogNotFound,
                "No SimpleRequestLog for CorrelationId {CorrelationId}; broadcast not addressed to SimpleFlow, skipping.",
                clientRequestId
            );
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.BlobName))
        {
            _logger.LogError(
                SimpleEvents.SimpleSubscriberBlobNameMissing,
                "SimpleRequestLog entry is missing BlobName"
            );
            throw new Exception("SimpleRequestLog entry is missing BlobName");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            SimpleEvents.SimpleSubscriberMoveToFinal,
            "Moving {BlobName} to Final",
            entry.BlobName
        );
        await _blobTool.MoveAsync(entry.BlobName, _config.WorkingContainer, _config.FinalContainer, cancellationToken);

        entry.ApiId = message.NodeId;

        _logger.LogInformation(
            SimpleEvents.SimpleSubscriberUpdateRequestLog,
            "Update RequestLog with {ApiId}",
            entry.ApiId
        );
        await _requestLogRepo.UpdateAsync(entry);

        _logger.LogDebug(
            SimpleEvents.SimpleSubscriberCompleted,
            "Processed Simple Request Log: {@entry}",
            entry
        );
    }
}
