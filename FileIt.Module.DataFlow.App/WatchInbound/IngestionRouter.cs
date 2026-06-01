using FileIt.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.DataFlow.App.WatchInbound;

/// <summary>
/// Decides, based on a landed file's NAME, where the file should go.
///
/// THINK OF THIS AS THE MAILROOM SORTER:
/// A file lands in blob storage. Its name tells us what it is. A GLAccount file
/// gets the built-in C# transform. A BIC_ActiveCustomer file gets handed off to
/// the Databricks Salesforce job (which replaced the old SSIS package). This is
/// the exact pattern the legacy stack used: "a file shows up, look at its name,
/// run the matching job."
///
/// This router ONLY handles the "should this go to Databricks?" decision. If it
/// says yes, it fires the Databricks job and reports back "handled, stop here."
/// If it says no, the caller continues with the normal in-FileIt processing.
/// </summary>
public interface IIngestionRouter
{
    /// <summary>
    /// Looks at the filename and, if it matches a Databricks route, triggers the
    /// matching Databricks job.
    /// </summary>
    /// <returns>
    /// true  = "I handled this; it went to Databricks. Caller should STOP."
    /// false = "Not mine; caller should run the normal FileIt transform."
    /// </returns>
    Task<bool> TryRouteToDatabricksAsync(
        string blobName,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public class IngestionRouter : IIngestionRouter
{
    private readonly ILogger<IngestionRouter> _logger;
    private readonly IDatabricksJobClient _databricks;
    private readonly DataFlowConfig _config;

    public IngestionRouter(
        ILogger<IngestionRouter> logger,
        IDatabricksJobClient databricks,
        DataFlowConfig config)
    {
        _logger = logger;
        _databricks = databricks;
        _config = config;
    }

    public async Task<bool> TryRouteToDatabricksAsync(
        string blobName,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        // THE DISPATCH TABLE.
        // Right now it's one rule: a file whose name starts with
        // "BIC_ActiveCustomer" goes to the Salesforce Databricks job. To add
        // more routes later (e.g. another extract -> another job), add another
        // check here, or graduate this to a config-driven map. Kept as a simple
        // explicit check so it's dead obvious what routes where.
        //
        // We compare on the file name only (strip any folder prefix) and ignore
        // case, so "BIC_ActiveCustomer.txt", "bic_activecustomer.TXT", and a
        // path like "uploads/BIC_ActiveCustomer.txt" all match.
        var fileName = blobName.Split('/').Last();

        if (fileName.StartsWith("BIC_ActiveCustomer", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "File {BlobName} matched the Databricks Salesforce route. Triggering job {JobId}.",
                blobName, _config.SalesforceDatabricksJobId);

            // Fire the Databricks job. We pass the SAME correlationId so the
            // Databricks run can be traced back to this file-arrival event.
            // filePath is just the file name; the notebook reads it from blob.
            await _databricks.RunJobNowAsync(
                _config.SalesforceDatabricksJobId,
                correlationId,
                fileName,
                cancellationToken);

            return true; // handled, caller stops
        }

        // No Databricks route matched. Caller continues with the normal flow.
        return false;
    }
}
