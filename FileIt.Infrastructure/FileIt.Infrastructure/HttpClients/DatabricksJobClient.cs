using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileIt.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileIt.Infrastructure.HttpClients;

/// <summary>
/// The REAL worker that fulfills the IDatabricksJobClient contract.
///
/// Its whole job: make one HTTP POST to the Databricks "Jobs API" that says
/// "start job number X now, and here are two parameters for it." Databricks
/// starts the job on its own cluster and immediately replies with a run id.
/// We do NOT wait for the ETL to finish; that could take minutes. We fire it
/// and return.
///
/// HOW IT TALKS TO DATABRICKS:
/// - It uses an HttpClient that was pre-configured (in Program.cs) with:
///     (a) the BaseAddress = your Databricks workspace URL, and
///     (b) an "Authorization: Bearer <token>" header = your Databricks
///         personal access token (PAT).
///   So this class never has to know the URL or the token; they're baked into
///   the HttpClient it's handed. That keeps secrets out of this file.
/// - The Databricks endpoint is POST /api/2.1/jobs/run-now.
/// - The JSON body looks like:
///     { "job_id": 915776115100975,
///       "notebook_params": { "correlationId": "...", "filePath": "..." } }
///   "notebook_params" is how Databricks passes values into the notebook's
///   dbutils.widgets.get("correlationId") / get("filePath") calls.
/// </summary>
public class DatabricksJobClient : IDatabricksJobClient
{
    // Tells the JSON serializer to use snake_case names like "job_id" and
    // "notebook_params", because that's what the Databricks API expects.
    // (Our C# properties are PascalCase; this maps between the two worlds.)
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly ILogger<DatabricksJobClient> _logger;

    // The framework hands us a ready-to-use HttpClient (already pointed at
    // Databricks with the token attached) and a logger. We just hold onto them.
    public DatabricksJobClient(HttpClient http, ILogger<DatabricksJobClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Starts the given Databricks job right now and returns the run id.
    /// </summary>
    public async Task<DatabricksRunResult> RunJobNowAsync(
        long jobId,
        string correlationId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        // Build the request body. This shape is dictated by the Databricks API,
        // not by us. NotebookParams becomes "notebook_params" in JSON, and its
        // keys ("correlationId", "filePath") are exactly the widget names the
        // notebook reads.
        var body = new RunNowRequest
        {
            JobId = jobId,
            JobParameters = new Dictionary<string, string>
            {
                ["correlationId"] = correlationId,
                ["filePath"] = filePath,
            },
        };
        _logger.LogInformation(
            "Triggering Databricks job {JobId} for file {FilePath} with CorrelationId {CorrelationId}",
            jobId, filePath, correlationId);

        // Fire the POST. JsonContent.Create turns our object into the snake_case
        // JSON Databricks wants. SendAsync sends it; the token is already on the
        // HttpClient so we don't add it here.
        using var resp = await _http
            .PostAsJsonAsync("api/2.1/jobs/run-now", body, JsonOpts, cancellationToken)
            .ConfigureAwait(false);

        // If Databricks rejected the call (bad token, bad job id, etc.), this
        // throws an exception with the status code. We let it bubble up so the
        // caller (the Watcher) can log and react, same pattern as ComplexApiClient.
        resp.EnsureSuccessStatusCode();

        // Databricks replies with JSON like { "run_id": 123, "number_in_job": 5 }.
        // Read it into our small wire object.
        var dto = await resp.Content
            .ReadFromJsonAsync<RunNowResponse>(JsonOpts, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Databricks run-now returned an empty body.");

        _logger.LogInformation(
            "Databricks job {JobId} started. RunId {RunId} (run #{NumberInJob}) CorrelationId {CorrelationId}",
            jobId, dto.RunId, dto.NumberInJob, correlationId);

        // Hand back the clean little result the interface promised.
        return new DatabricksRunResult(dto.RunId, dto.NumberInJob);
    }

    // ---- Wire shapes: these exist ONLY to match the JSON Databricks sends and
    // receives. They're private because nothing outside this file should care
    // about the raw API format. ----

    // What we SEND.
    private sealed class RunNowRequest
    {
        // Serializes to "job_id".
        public long JobId { get; set; }

        // Serializes to "job_parameters". Required (instead of the legacy
        // "notebook_params") because the Terraform job defines job-level
        // parameter blocks. Databricks rejects notebook_params when job
        // parameters are configured.
        public Dictionary<string, string> JobParameters { get; set; } = new();
    }

    // What we GET BACK. We only bother to read the two fields we care about;
    // Databricks sends more, but unlisted fields are simply ignored.
    private sealed class RunNowResponse
    {
        // "run_id" -> the id of this specific run.
        [JsonPropertyName("run_id")]
        public long RunId { get; set; }

        // "number_in_job" -> human-friendly "this is run #N of the job".
        [JsonPropertyName("number_in_job")]
        public long NumberInJob { get; set; }
    }
}
