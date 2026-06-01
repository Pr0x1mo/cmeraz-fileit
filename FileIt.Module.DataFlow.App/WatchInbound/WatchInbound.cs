// This is the entry point for the DataFlow module.
// It gets triggered when a new GL Account CSV file lands in the source blob container.
// It does three things: logs the incoming file, moves it to working, 
// and puts a message on the service bus to kick off the transform.
using FileIt.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.DataFlow.App.WatchInbound;

public interface IWatchInbound
{
    Task RunAsync(string blobName, string correlationId, CancellationToken cancellationToken = default);
}

public class WatchInbound : IWatchInbound
{
    private readonly ILogger<WatchInbound> _logger;
    private readonly IHandleFiles _blobTool;
    private readonly ITalkToApi _busTool;
    private readonly DataFlowConfig _config;
    private readonly IDataFlowRequestLogRepo _requestLogRepo;
    private readonly IIngestionRouter _router;

    public WatchInbound(
        ILogger<WatchInbound> logger,
        IHandleFiles blobTool,
        ITalkToApi busTool,
        IDataFlowRequestLogRepo requestLogRepo,
        DataFlowConfig config,
        IIngestionRouter router
    )
    {
        _blobTool = blobTool;
        _busTool = busTool;
        _config = config;
        _logger = logger;
        _requestLogRepo = requestLogRepo;
        _router = router;
    }

    public async Task RunAsync(string blobName, string correlationId, CancellationToken cancellationToken = default)
    {
        // Step 0 - routing decision. Before we do anything GLAccount-specific,
        // ask the router whether this file's NAME means it belongs to a
        // downstream system (e.g. a BIC_ActiveCustomer file goes to the
        // Databricks Salesforce job, not the in-FileIt C# transform). If the
        // router handled it, we stop here. The file-arrival is still logged by
        // the Watcher that called us, and the Databricks run carries the same
        // CorrelationId, so the trace stays intact across both systems.
        if (await _router.TryRouteToDatabricksAsync(blobName, correlationId, cancellationToken))
        {
            _logger.LogInformation(
                DataFlowEvents.DataFlowWatcherQueueTransform,
                "File {BlobName} routed to Databricks; skipping in-FileIt transform.",
                blobName
            );
            return;
        }
        _logger.LogInformation(
            DataFlowEvents.DataFlowWatcherAddRequestLog,
            "Adding RequestLog for {BlobName}",
            blobName
        );
        await _requestLogRepo.AddAsync(blobName, correlationId);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2 - move the file out of source into working so nothing else picks it up
        _logger.LogInformation(
            DataFlowEvents.DataFlowWatcherMoveToWorking,
            "Moving {BlobName} to working container",
            blobName
        );
        await _blobTool.MoveAsync(blobName, _config.SourceContainer, _config.WorkingContainer, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 3 - put a message on the service bus queue so the transform handler knows there's work to do
        string messageId = Guid.NewGuid().ToString();
        _logger.LogInformation(
            DataFlowEvents.DataFlowWatcherQueueTransform,
            "Queuing transform message for {BlobName}",
            blobName
        );
        await _busTool.SendMessageAsync(
            new Domain.Entities.Api.ApiRequest(messageId)
            {
                Body = new DataFlowMessage() { BlobName = blobName },
                ReplyTo = _config.TransformTopicName,
                CorrelationId = correlationId,
                QueueName = _config.TransformQueueName,
            }
        );
    }
}
