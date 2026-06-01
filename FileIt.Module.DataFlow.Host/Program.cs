using FileIt.Domain.Interfaces;
using FileIt.Infrastructure.Extensions;
using FileIt.Infrastructure.HttpClients;
using FileIt.Infrastructure.Logging;
using FileIt.Infrastructure.Middleware;
using FileIt.Module.DataFlow.App;
using FileIt.Module.DataFlow.App.Transform;
using FileIt.Module.DataFlow.App.WatchInbound;
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

#if RELEASE
// Add Application Insights telemetry if deployed to Azure
builder.Services.AddApplicationInsightsTelemetryWorkerService();
#endif

// Load our DataFlow config from appsettings
var sectionName = builder.Configuration.GetValue<string>("FeatureSection") ?? "Feature";
DataFlowConfig? config = builder.Configuration.GetSection(sectionName).Get<DataFlowConfig>();
if (config == null)
{
    throw new ApplicationException("Appsettings.json is missing Feature config.");
}

builder.Services.AddSingleton(config);

// Register our DataFlow handlers
// Register our DataFlow handlers
builder.Services.AddScoped<IWatchInbound, WatchInbound>();
builder.Services.AddScoped<ITransformGlAccounts, TransformGlAccounts>();
builder.Services.AddScoped<IIngestionRouter, IngestionRouter>();

// Wire up the shared infrastructure (blob, service bus, database)
var infrastructureConfig = builder.GetInfrastructureConfig();
builder.Services.AddInfrastructure(infrastructureConfig);

// Wire up the Databricks job client. This is a "typed HttpClient": the
// framework creates an HttpClient pre-loaded with the Databricks workspace URL
// (BaseAddress) and the access token (Authorization header), then hands it to
// DatabricksJobClient. So the client class itself never sees the URL or token;
// they come from app settings here. Same pattern as the Complex API client.
//
// DatabricksWorkspaceUrl and DatabricksToken come from appsettings/environment
// (double-underscore form in Azure: DatabricksWorkspaceUrl, DatabricksToken).
builder.Services.AddHttpClient<IDatabricksJobClient, DatabricksJobClient>(client =>
{
    var workspaceUrl = builder.Configuration.GetValue<string>("DatabricksWorkspaceUrl")
        ?? throw new ApplicationException(
            "Missing DatabricksWorkspaceUrl. Set it in app settings (the Databricks workspace base URL).");
    var token = builder.Configuration.GetValue<string>("DatabricksToken")
        ?? throw new ApplicationException(
            "Missing DatabricksToken. Set it in app settings (a Databricks personal access token).");

    if (!workspaceUrl.EndsWith('/'))
    {
        workspaceUrl += "/";
    }
    client.BaseAddress = new Uri(workspaceUrl);
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Configure logging
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
