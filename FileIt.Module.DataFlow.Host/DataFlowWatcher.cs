
// Watches for new GL Account CSV files in blob storage.
// Local dev (DEBUG): direct blob trigger. Production: Event Grid trigger.
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using FileIt.Infrastructure.Extensions;
using FileIt.Module.DataFlow.App;
using FileIt.Module.DataFlow.App.WatchInbound;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.DataFlow.Host;

public class DataFlowWatcher
{
    private readonly DataFlowConfig _config;
    private readonly ILogger<DataFlowWatcher> _logger;
    private readonly IWatchInbound _watcher;

    public DataFlowWatcher(ILogger<DataFlowWatcher> logger, DataFlowConfig config, IWatchInbound watcher)
    {
        _config = config;
        _logger = logger;
        _watcher = watcher;
    }

#if DEBUG
    [Function("DataFlowWatcherLocal")]
    public async Task RunLocal(
        [BlobTrigger("dataflow-source/{blobName}")] BlobClient blobClient,
        string blobName,
        FunctionContext context
    )
    {
        blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        var cancellationToken = context.CancellationToken;
        string clientRequestId = await blobClient.GetCorrelationId();
        using (_logger!.BeginScope(new Dictionary<string, object>() { { "CorrelationId", clientRequestId } }))
        {
            _logger.LogInformation(DataFlowEvents.DataFlowWatcher, "Received blob trigger for blob: {BlobName}", blobName);
            await _watcher.RunAsync(blobName, clientRequestId, cancellationToken);
        }
    }
#endif

    [Function(nameof(DataFlowWatcher))]
    public async Task Run([EventGridTrigger] EventGridEvent eventGridEvent, FunctionContext context)
    {
        var cancellationToken = context.CancellationToken;
        _logger.LogInformation("Received EventGridEvent: {@EventGridEvent}", eventGridEvent);
        var blobName = (eventGridEvent.Subject ?? string.Empty).Split('/').Last();

        // Prefer the correlation id the uploader stamped into blob metadata (e.g. the
        // operator UI) so the whole flow shares one id end to end. Fall back to the
        // EventGrid event id only when no metadata id is present.
        string clientRequestId = eventGridEvent.Id;
        try
        {
            var storageUri = Environment.GetEnvironmentVariable("FileItStorage__serviceUri") ?? string.Empty;
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            var blobClient = new BlobContainerClient(
                new Uri(new Uri(storageUri), _config.SourceContainer + "/"),
                new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId }))
                .GetBlobClient(blobName);
            var metaId = await blobClient.GetCorrelationId();
            if (!string.IsNullOrWhiteSpace(metaId))
            {
                clientRequestId = metaId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read blob metadata correlation id for {BlobName}, using EventGrid id.", blobName);
        }

        using (_logger!.BeginScope(new Dictionary<string, object>() { { "CorrelationId", clientRequestId } }))
        {
            _logger.LogInformation(DataFlowEvents.DataFlowWatcher, "Received blob trigger for blob: {BlobName}", blobName);
            await _watcher.RunAsync(blobName, clientRequestId, cancellationToken);
        }
    }
}
