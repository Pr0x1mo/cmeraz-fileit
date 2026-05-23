using FileIt.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Ui.Host;

// Operator-facing replay trigger. The actual replay work lives in the services FA
// at POST /api/deadletter/{id}/replay (from #22). The Ui just relays so the front
// end has a single origin to talk to.
public class ReplayApi
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<ReplayApi> _logger;

    public ReplayApi(IHttpClientFactory httpFactory, Microsoft.Extensions.Configuration.IConfiguration config, ILogger<ReplayApi> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    [Function("ReplayDeadLetter")]
    public async Task<IActionResult> ReplayDeadLetter(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deadletters/{id:long}/replay")] HttpRequest req,
        long id,
        CancellationToken ct)
    {
        var servicesHost = _config["DemoTargets:ServicesHost"]
            ?? throw new InvalidOperationException("DemoTargets:ServicesHost not configured");
        var url = $"{servicesHost}/api/deadletter/{id}/replay";
        var client = _httpFactory.CreateClient("demo");
        var resp = await client.PostAsync(url, content: null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ContentResult { Content = body, StatusCode = (int)resp.StatusCode, ContentType = "application/json" };
    }
}