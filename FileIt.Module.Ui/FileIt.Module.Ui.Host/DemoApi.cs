using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Ui.Host;

public class DemoApi
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoApi> _logger;
    private readonly FileIt.Domain.Interfaces.IDatabricksJobClient _databricks;
    private readonly long _salesforceJobId;

    private static readonly EventId DemoClickEvent = new(9000, "DemoClick");
    private static readonly EventId QuarantineEvent = new(9001, "Quarantine");

    public DemoApi(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<DemoApi> logger,
        FileIt.Domain.Interfaces.IDatabricksJobClient databricks)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        _databricks = databricks;
        _salesforceJobId = config.GetValue<long>("SalesforceDatabricksJobId");
    }

    [Function("DemoDropCsv")]
    public async Task<IActionResult> DropCsv(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/drop-csv")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = $"demo-{correlationId}.csv";
        await CopySeedAsync("dataflow", "seeds", "GLAccount.csv", "dataflow-source", fileName, correlationId, ct);

        var humanFileName = $"GLAccount-{DateTime.UtcNow:yyyy-MM-dd-HHmm-ss}.csv";
        _logger.LogInformation(DemoClickEvent,
            "Demo click: {OriginalFileName} fired by Drop CSV with CorrelationId {CorrelationId}",
            humanFileName, correlationId);

        return new OkObjectResult(new { correlationId, fileName = humanFileName, storedAs = fileName, module = "dataflow", pattern = "happy" });
    }

    [Function("DemoSendApi")]
    public async Task<IActionResult> SendApi(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/send-api")] HttpRequest req,
        CancellationToken ct)
    {
        var servicesHost = _config["DemoTargets:ServicesHost"]
            ?? throw new InvalidOperationException("DemoTargets:ServicesHost not configured");
        var url = $"{servicesHost}/api/test/api-add";
        var payload = JsonSerializer.Serialize(new { fileName = "demo.txt" });
        var client = _httpFactory.CreateClient("demo");
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        string correlationId = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("correlationId", out var cid))
                correlationId = cid.GetString() ?? "";
        }
        catch { }

        var humanFileName = $"demo-{DateTime.UtcNow:yyyy-MM-dd-HHmm-ss}.txt";
        _logger.LogInformation(DemoClickEvent,
            "Demo click: {OriginalFileName} fired by Send API with CorrelationId {CorrelationId}",
            humanFileName, correlationId);
        return new OkObjectResult(new { correlationId, fileName = humanFileName, module = "services+complex", pattern = "request-response", upstream = (int)resp.StatusCode });
    }

    [Function("DemoPublishBroadcast")]
    public async Task<IActionResult> PublishBroadcast(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/publish-broadcast")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = $"demo-{correlationId}.txt";
        var body = $"demo-broadcast,{DateTime.UtcNow:O},{correlationId}\n";
        await UploadTextAsync("simple", "simple-source", fileName, body, correlationId, ct);

        var humanFileName = $"broadcast-{DateTime.UtcNow:yyyy-MM-dd-HHmm-ss}.txt";
        _logger.LogInformation(DemoClickEvent,
            "Demo click: {OriginalFileName} fired by Publish Broadcast with CorrelationId {CorrelationId}",
            humanFileName, correlationId);

        return new OkObjectResult(new { correlationId, fileName = humanFileName, storedAs = fileName, module = "simple", pattern = "pub-sub" });
    }

    [Function("DemoDropPoison")]
    public async Task<IActionResult> DropPoison(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/drop-poison")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = $"poison-{correlationId}.csv";
        await CopySeedAsync("dataflow", "seeds", "GLAccount-POISONED.csv", "dataflow-source", fileName, correlationId, ct);

        var humanFileName = $"GLAccount-POISONED-{DateTime.UtcNow:yyyy-MM-dd-HHmm-ss}.csv";
        _logger.LogInformation(DemoClickEvent,
            "Demo click: {OriginalFileName} fired by Drop Poison with CorrelationId {CorrelationId}",
            humanFileName, correlationId);

        return new OkObjectResult(new { correlationId, fileName = humanFileName, storedAs = fileName, module = "dataflow", pattern = "dlq" });
    }

    [Function("DemoRunSalesforce")]
    public async Task<IActionResult> RunSalesforce(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/run-salesforce")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = "BIC_ActiveCustomer.txt";
        var humanFileName = $"BIC_ActiveCustomer-{DateTime.UtcNow:yyyy-MM-dd-HHmm-ss}.txt";

        await CopySeedAsync("ui", "seeds", fileName, "salesforce", fileName, correlationId, ct);

        var run = await _databricks.RunJobNowAsync(_salesforceJobId, correlationId, fileName, ct);

        _logger.LogInformation(DemoClickEvent,
            "Demo click: {OriginalFileName} fired by Run Salesforce ETL with CorrelationId {CorrelationId}",
            humanFileName, correlationId);

        return new OkObjectResult(new
        {
            correlationId,
            fileName = humanFileName,
            storedAs = fileName,
            module = "databricks-salesforce",
            pattern = "etl-handoff",
            databricksRunId = run.RunId
        });
    }

    [Function("DemoUploadFile")]
    public async Task<IActionResult> UploadFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/upload")] HttpRequest req,
        CancellationToken ct)
    {
        if (!req.HasFormContentType || req.Form.Files.Count == 0)
            return new BadRequestObjectResult(new { error = "no file in form" });

        var file = req.Form.Files[0];
        var module = req.Form["module"].ToString();
        if (string.IsNullOrEmpty(module)) module = "dataflow";

        if (module != "dataflow" && module != "salesforce")
        {
            return new BadRequestObjectResult(new
            {
                error = $"Upload Your Own only accepts module=dataflow (GLAccount CSV) or module=salesforce (BIC_ActiveCustomer txt). Got '{module}'.",
                allowed = new[] { "dataflow", "salesforce" }
            });
        }

        var correlationId = Guid.NewGuid().ToString();

        // Open the form file as a seekable stream. IFormFile.OpenReadStream returns a
        // stream backed by either memory or a temp file on disk depending on size, so
        // it scales to large files without buffering the whole thing into a byte[].
        using var stream = file.OpenReadStream();

        // Stage 1: header-only check. Reads ~1 KB to grab the first line, rewinds.
        // Bad column count rejects in milliseconds even on a 500 MB file.
        var stage1 = await FileItValidator.ValidateHeaderAsync(stream, file.FileName, module, ct);

        // Stage 2: row-by-row, column-by-column. Streams through the file with a
        // StreamReader so memory stays small. Stops after 20 errors regardless of
        // file size.
        ValidationResult? stage2 = null;
        if (stage1.IsValid)
            stage2 = await FileItValidator.ValidateRowsAsync(stream, module, ct);

        var allErrors = new List<string>();
        allErrors.AddRange(stage1.Errors);
        if (stage2 != null) allErrors.AddRange(stage2.Errors);

        if (allErrors.Count > 0)
        {
            var quarantineBlobName = $"quarantine-{correlationId}-{file.FileName}";
            stream.Position = 0;
            await QuarantineStreamAsync(module, quarantineBlobName, stream, allErrors, correlationId, file.FileName, ct);

            _logger.LogWarning(QuarantineEvent,
                "Quarantined: {OriginalFileName} failed validation with {ErrorCount} errors. CorrelationId {CorrelationId}",
                file.FileName, allErrors.Count, correlationId);

            return new BadRequestObjectResult(new
            {
                correlationId,
                fileName = file.FileName,
                quarantinedAs = quarantineBlobName,
                module,
                pattern = "quarantine",
                errors = allErrors,
                rowCount = stage2?.RowCount ?? 0,
                columnCount = stage1.ColumnCount,
                stagesFailed = stage2 == null ? new[] { stage1.Stage! } : new[] { stage1.Stage!, stage2.Stage! }
            });
        }

        // Validation passed. Upload to the real source container.
        stream.Position = 0;
        if (module == "dataflow")
        {
            var container = $"{module}-source";
            var blobName = $"upload-{correlationId}-{file.FileName}";
            var uri = _config[$"DemoTargets:{module}StorageUri"]
                ?? throw new InvalidOperationException($"DemoTargets:{module}StorageUri not configured");
            var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
            var blobClient = serviceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: ct);
            await blobClient.SetMetadataAsync(new Dictionary<string, string> { { "correlationId", correlationId } }, cancellationToken: ct);

            _logger.LogInformation(DemoClickEvent,
                "Demo click: {OriginalFileName} fired by Upload with CorrelationId {CorrelationId}",
                file.FileName, correlationId);

            return new OkObjectResult(new { correlationId, fileName = file.FileName, storedAs = blobName, module, pattern = "upload", bytes = file.Length });
        }
        else // salesforce
        {
            var uri = _config["DemoTargets:uiStorageUri"]
                ?? throw new InvalidOperationException("DemoTargets:uiStorageUri not configured");
            var blobName = $"upload-{correlationId}-{file.FileName}";
            var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
            var blobClient = serviceClient.GetBlobContainerClient("salesforce").GetBlobClient(blobName);
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: ct);
            await blobClient.SetMetadataAsync(new Dictionary<string, string> { { "correlationId", correlationId } }, cancellationToken: ct);

            var run = await _databricks.RunJobNowAsync(_salesforceJobId, correlationId, blobName, ct);

            _logger.LogInformation(DemoClickEvent,
                "Demo click: {OriginalFileName} fired by Upload (Salesforce) with CorrelationId {CorrelationId}",
                file.FileName, correlationId);

            return new OkObjectResult(new
            {
                correlationId,
                fileName = file.FileName,
                storedAs = blobName,
                module = "databricks-salesforce",
                pattern = "etl-handoff",
                databricksRunId = run.RunId
            });
        }
    }

    private async Task QuarantineStreamAsync(
        string module,
        string blobName,
        Stream fileStream,
        List<string> errors,
        string correlationId,
        string originalFileName,
        CancellationToken ct)
    {
        var configKey = module == "salesforce" ? "DemoTargets:uiStorageUri" : $"DemoTargets:{module}StorageUri";
        var uri = _config[configKey]
            ?? throw new InvalidOperationException($"{configKey} not configured");
        var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
        var container = serviceClient.GetBlobContainerClient($"{module}-quarantine");
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var blob = container.GetBlobClient(blobName);
        var opts = new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string>
            {
                { "correlationId", correlationId },
                { "originalFileName", originalFileName },
                { "errorCount", errors.Count.ToString() }
            }
        };
        await blob.UploadAsync(fileStream, opts, ct);

        var report = new
        {
            correlationId,
            originalFileName,
            rejectedAt = DateTime.UtcNow,
            errors
        };
        var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportBlobName = blobName + ".errors.json";
        var reportBlob = container.GetBlobClient(reportBlobName);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(reportJson)))
            await reportBlob.UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    private async Task CopySeedAsync(string module, string seedContainer, string seedBlob, string destContainer, string destBlob, string correlationId, CancellationToken ct)
    {
        var uri = _config[$"DemoTargets:{module}StorageUri"]
            ?? throw new InvalidOperationException($"DemoTargets:{module}StorageUri not configured");
        var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
        var src = serviceClient.GetBlobContainerClient(seedContainer).GetBlobClient(seedBlob);
        var dest = serviceClient.GetBlobContainerClient(destContainer).GetBlobClient(destBlob);

        var download = await src.DownloadStreamingAsync(cancellationToken: ct);
        var options = new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string> { { "correlationId", correlationId } }
        };
        await dest.UploadAsync(download.Value.Content, options, ct);
        _logger.LogInformation("Copied seed {Seed} to {Dest} on {Module} with correlationId {CorrelationId}", seedBlob, destBlob, module, correlationId);
    }

    private async Task UploadTextAsync(string module, string container, string blobName, string content, string correlationId, CancellationToken ct)
    {
        var uri = _config[$"DemoTargets:{module}StorageUri"]
            ?? throw new InvalidOperationException($"DemoTargets:{module}StorageUri not configured");
        var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
        var blobClient = serviceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
        var bytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream(bytes);
        var options = new BlobUploadOptions { Metadata = new Dictionary<string, string> { { "correlationId", correlationId } } };
        await blobClient.UploadAsync(ms, options, ct);
    }
}
