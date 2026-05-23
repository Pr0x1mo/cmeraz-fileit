using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Ui;

public class Health
{
    private readonly ILogger<Health> _logger;

    public Health(ILogger<Health> logger)
    {
        _logger = logger;
    }

    [Function(nameof(Health))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req
    )
    {
        using (
            _logger!.BeginScope(new Dictionary<string, object>() { { "Trigger", "HealthHttp" } })
        )
        {
            _logger.LogInformation("Health HTTP trigger started");
            var response = req.CreateResponse();
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            response.StatusCode = System.Net.HttpStatusCode.OK;
            await response.WriteStringAsync("Health OK");
            await Task.CompletedTask;
            return response;
        }
    }
}
