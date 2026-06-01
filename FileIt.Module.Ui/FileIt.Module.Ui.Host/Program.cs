using FileIt.Infrastructure.Extensions;
using FileIt.Infrastructure.Logging;
using FileIt.Infrastructure.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.UseMiddleware<MiddlewareLogger>();
builder.UseMiddleware<SerilogInvocationIdMiddleware>();
builder.UseMiddleware<ExceptionHandlingMiddleware>();

var infrastructureConfig = builder.GetInfrastructureConfig();
builder.Services.AddInfrastructure(infrastructureConfig);

// HttpClient used by the demo trigger endpoints to call the other FAs' test producers and blob uploads
builder.Services.AddHttpClient("demo");
// Databricks job client, so the Run Salesforce ETL button can fire the
// Databricks Workflow directly. Pre-loads the workspace URL and token from
// app settings onto the HttpClient (same pattern as the dataflow host).
builder.Services.AddHttpClient<FileIt.Domain.Interfaces.IDatabricksJobClient, FileIt.Infrastructure.HttpClients.DatabricksJobClient>(client =>
{
    var workspaceUrl = builder.Configuration.GetValue<string>("DatabricksWorkspaceUrl")
        ?? throw new ApplicationException("Missing DatabricksWorkspaceUrl.");
    var token = builder.Configuration.GetValue<string>("DatabricksToken")
        ?? throw new ApplicationException("Missing DatabricksToken.");
    if (!workspaceUrl.EndsWith('/')) workspaceUrl += "/";
    client.BaseAddress = new Uri(workspaceUrl);
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Logging.ClearProviders();
ICommonLogConfig logConfig = builder.Configuration.GetCommonLogConfig();
logConfig.Environment = logConfig.Environment ?? builder.Environment.EnvironmentName;
logConfig.Application = builder.Environment.ApplicationName;
logConfig.ApplicationVersion = System
    .Reflection.Assembly.GetExecutingAssembly()
    .GetName()
    .Version?.ToString();
builder.Services.AddSingleton(logConfig);
builder.Logging.AddCommonLog(logConfig);

builder.Build().Run();
