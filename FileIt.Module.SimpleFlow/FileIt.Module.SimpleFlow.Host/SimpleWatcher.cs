using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using FileIt.Domain.Entities;
using FileIt.Domain.Interfaces;
using FileIt.Infrastructure.Extensions;
using FileIt.Module.SimpleFlow.App;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.EventGrid;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.SimpleFlow.Host;

public class SimpleWatcher
{
    private readonly SimpleConfig _config;
    private readonly ILogger<SimpleWatcher> _logger;
    private readonly IWatchInbound _watcher;

    public SimpleWatcher(ILogger<SimpleWatcher> logger, SimpleConfig config, IWatchInbound watcher)
    {
        _config = config;
        _logger = logger;
        _watcher = watcher;
    }

#if DEBUG
    [Function("SimpleWatcherLocal")]
    public async Task RunLocal(
        [BlobTrigger("simple-source/{blobName}")] BlobClient blobClient,
        string blobName,
        FunctionContext context
    )
    {
        var cancellationToken = context.CancellationToken;
        blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));

        string clientRequestId = await blobClient.GetCorrelationId();

        using (
            _logger!.BeginScope(
                new Dictionary<string, object>() { { "CorrelationId", clientRequestId } }
            )
        )
        {
            _logger.LogInformation(
                SimpleEvents.SimpleWatcher,
                "Received blob trigger for blob: {BlobName}",
                blobName
            );

            cancellationToken.ThrowIfCancellationRequested();

            await _watcher.RunAsync(blobName, clientRequestId, cancellationToken);
        }
    }
#endif

    [Function(nameof(SimpleWatcher))]
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

        using (
            _logger!.BeginScope(
                new Dictionary<string, object>() { { "CorrelationId", clientRequestId } }
            )
        )
        {
            _logger.LogInformation(
                SimpleEvents.SimpleWatcher,
                "Received blob trigger for blob: {BlobName}",
                blobName
            );

            cancellationToken.ThrowIfCancellationRequested();

            await _watcher.RunAsync(blobName, clientRequestId, cancellationToken);
        }
    }
}
