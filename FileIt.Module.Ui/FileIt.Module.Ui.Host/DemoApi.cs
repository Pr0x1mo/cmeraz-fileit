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
        return new OkObjectResult(new { correlationId, fileName, module = "dataflow", pattern = "happy" });
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

        // The services test producer generates its own CorrelationId and returns it. We
        // surface that id (not a UI-generated one) so the timeline lookup matches the
        // rows the services and complex modules actually wrote.
        string correlationId = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("correlationId", out var cid))
                correlationId = cid.GetString() ?? "";
        }
        catch { /* leave blank if the producer response isn't JSON */ }

        return new OkObjectResult(new { correlationId, module = "services+complex", pattern = "request-response", upstream = (int)resp.StatusCode });
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
        return new OkObjectResult(new { correlationId, fileName, module = "simple", pattern = "pub-sub" });
    }

    [Function("DemoDropPoison")]
    public async Task<IActionResult> DropPoison(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/drop-poison")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = $"poison-{correlationId}.csv";
        await CopySeedAsync("dataflow", "seeds", "GLAccount-POISONED.csv", "dataflow-source", fileName, correlationId, ct);
        return new OkObjectResult(new { correlationId, fileName, module = "dataflow", pattern = "dlq" });
    }
    [Function("DemoRunSalesforce")]
    public async Task<IActionResult> RunSalesforce(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/run-salesforce")] HttpRequest req,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var fileName = "BIC_ActiveCustomer.txt";

        // Copy the seeded BIC file into the salesforce container with a fresh
        // correlationId in metadata, same seed-copy pattern as the CSV demos.
        await CopySeedAsync("ui", "seeds", fileName, "salesforce", fileName, correlationId, ct);

        // Fire the Databricks Salesforce job directly, passing the same
        // correlationId so the Databricks run ties back to this trigger.
        var run = await _databricks.RunJobNowAsync(
            _salesforceJobId, correlationId, fileName, ct);

        return new OkObjectResult(new
        {
            correlationId,
            fileName,
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
        var container = $"{module}-source";
        var correlationId = Guid.NewGuid().ToString();
        var blobName = $"upload-{correlationId}-{file.FileName}";
        var uri = _config[$"DemoTargets:{module}StorageUri"]
            ?? throw new InvalidOperationException($"DemoTargets:{module}StorageUri not configured");
        var serviceClient = new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());
        var blobClient = serviceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
        using var s = file.OpenReadStream();
        await blobClient.UploadAsync(s, overwrite: true, cancellationToken: ct);
        await blobClient.SetMetadataAsync(new Dictionary<string, string> { { "correlationId", correlationId } }, cancellationToken: ct);
        return new OkObjectResult(new { correlationId, fileName = blobName, module, pattern = "upload", bytes = file.Length });
    }

    // Copy a known-good seed blob into the source container under a fresh name, stamping
    // the correlation id into metadata so the pipeline carries it end to end. No hardcoded
    // CSV content, so the demo always uses a real, valid file.
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
