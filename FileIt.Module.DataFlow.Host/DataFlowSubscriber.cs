// This function listens on the dataflow-transform queue for files ready to transform.
// It runs the GL Account transform directly via Infrastructure, no Services involved.
// Output CSV goes to dataflow-final, request log gets updated with results.
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FileIt.Domain.Common;
using FileIt.Domain.Interfaces;
using FileIt.Module.DataFlow.App;
using FileIt.Module.DataFlow.App.Transform;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.DataFlow.Host;

public class DataFlowSubscriber
{
    private readonly DataFlowConfig _config;
    private readonly ILogger<DataFlowSubscriber> _logger;
    private readonly ITransformGlAccounts _transformHandler;
    private readonly IDataFlowRequestLogRepo _requestLogRepo;
    private readonly IHandleFiles _blobTool;

    public DataFlowSubscriber(
        ILogger<DataFlowSubscriber> logger,
        DataFlowConfig config,
        ITransformGlAccounts transformHandler,
        IDataFlowRequestLogRepo requestLogRepo,
        IHandleFiles blobTool
    )
    {
        _config = config;
        _logger = logger;
        _transformHandler = transformHandler;
        _requestLogRepo = requestLogRepo;
        _blobTool = blobTool;
    }

    // Listens on the dataflow-transform queue for files ready to be transformed
    [Function(nameof(DataFlowSubscriber))]
    public async Task Run(
        [ServiceBusTrigger(MessagingNames.DataFlowTransformQueue)] ServiceBusReceivedMessage message,
        FunctionContext context
    )
    {
        var cancellationToken = context.CancellationToken;
        string clientRequestId = message.CorrelationId ?? string.Empty;

        using (
            _logger!.BeginScope(
                new Dictionary<string, object>()
                {
                    { "CorrelationId", clientRequestId ?? string.Empty },
                }
            )
        )
        {
            _logger.LogDebug(
                DataFlowEvents.DataFlowSubscriberReceive,
                "Receiving message for correlation {CorrelationId}",
                clientRequestId
            );

            // Look up the request log to get the blob name
            _logger.LogInformation(
                DataFlowEvents.DataFlowSubscriberGetRequestLog,
                "Looking up RequestLog for correlation {CorrelationId}",
                clientRequestId
            );

            var entry = await _requestLogRepo.GetByClientRequestIdAsync(clientRequestId);
            if (entry == null)
            {
                _logger.LogError(
                    DataFlowEvents.DataFlowSubscriberRequestLogNotFound,
                    "DataFlowRequestLog not found for correlation {CorrelationId}",
                    clientRequestId
                );
                throw new ApplicationException("DataFlowRequestLog entry not found");
            }

            if (string.IsNullOrWhiteSpace(entry.BlobName))
            {
                _logger.LogError(
                    DataFlowEvents.DataFlowSubscriberBlobNameMissing,
                    "BlobName is missing from DataFlowRequestLog"
                );
                throw new ApplicationException("DataFlowRequestLog entry is missing BlobName");
            }

            // Download the CSV from working container
            var csvStream = await _blobTool.DownloadAsync(entry.BlobName, _config.WorkingContainer, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Run the transform
            _logger.LogInformation(
                DataFlowEvents.DataFlowTransform,
                "Running GL Account transform for {BlobName}",
                entry.BlobName
            );

            string outputCsv = await _transformHandler.RunAsync(csvStream, clientRequestId, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Count rows in output
            var outputLines = outputCsv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            int rowsTransformed = outputLines.Length - 1;

            // Upload output CSV to final container
            string exportBlobName = $"summary_{entry.BlobName}";
            using var outputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(outputCsv));
            await _blobTool.UploadAsync(outputStream, exportBlobName, _config.FinalContainer, cancellationToken);

            _logger.LogInformation(
                DataFlowEvents.DataFlowSubscriberMoveToFinal,
                "Uploaded output file {ExportBlobName} to final container",
                exportBlobName
            );

            // Update the request log with transform results directly
            await _requestLogRepo.UpdateTransformResultAsync(
                clientRequestId,
                rowsTransformed,
                exportBlobName,
                "Complete"
            );

            _logger.LogInformation(
                DataFlowEvents.DataFlowSubscriberCompleted,
                "DataFlow transform complete. {RowsTransformed} groups written to {ExportBlobName}",
                rowsTransformed,
                exportBlobName
            );
        }
    }
}
