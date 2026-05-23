using System.Text;
using FileIt.Domain.Interfaces;
using FileIt.Infrastructure.DeadLetter.Ingestion;
using FileIt.Infrastructure.Logging;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Serilog;
using Serilog.Events;

// A static class to hold the extension method
public static class CommonLogExtensions
{
    // Extension method for ILoggingBuilder
    public static ICommonLogConfig GetCommonLogConfig(this IConfiguration configuration)
    {
        ICommonLogConfig logConfig = new CommonLogConfig();
        if (string.IsNullOrWhiteSpace(logConfig.Environment))
        {
            string? azure_env = configuration.GetValue<string>("AZURE_FUNCTIONS_ENVIRONMENT");
            logConfig.Environment = azure_env ?? logConfig.Environment;
        }
        logConfig.Host = Environment.MachineName;
        logConfig.Agent = Environment.UserName;
        logConfig.DbConnectionString =
            configuration.GetConnectionString("FileItDbConnection")
            ?? configuration.GetValue<string>("FileItDbConnection")
            ?? throw new ApplicationException("Connection Strings is missing FileItDbConnection.");

        string? logFilePath = configuration.GetValue<string>("LOG_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            logConfig.LogFilePath = logFilePath;
        }

        string? serilogFilePath = configuration.GetValue<string>("SERILOG_SELFLOG_FILE_PATH");
        if (!string.IsNullOrWhiteSpace(serilogFilePath))
        {
            logConfig.SerilogSelfLogFilePath = serilogFilePath;
        }
        return logConfig;
    }

    public static ILoggingBuilder AddCommonLog(
        this ILoggingBuilder builder,
        ICommonLogConfig featureConfig,
        string? selfLogFilePath = null
    )
    {
        var temp = new StringBuilder();
        temp.Append("{{");
        temp.Append("\n\t\"@t\":\"{Timestamp:o}\",");
        temp.Append("\n\t\"@l\":\"{Level}\",");
        temp.Append("\n\t\"Message\":\"{Message:lj}\",");
        temp.Append("\n\t\"MachineName\":\"{MachineName}\",");
        temp.Append("\n\t\"Application\":\"{Application}\",");
        temp.Append("\n\t\"ApplicationVersion\":\"{ApplicationVersion}\",");
        temp.Append("\n\t\"InfrastructureVersion\":\"{InfrastructureVersion}\",");
        temp.Append("\n\t\"SourceContext\":\"{SourceContext}\",");
        temp.Append("\n\t\"CorrelationId\":\"{CorrelationId}\",");
        temp.Append("\n\t\"InvocationId\":\"{InvocationId}\",");
        temp.Append("\n\t\"EventId\":{EventId},");
        temp.Append("\n\t\"EventName\":\"{EventName}\"");
        temp.Append("\n}}{NewLine}{Exception}");

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Override("Azure", LogEventLevel.Warning)
            .MinimumLevel.Override("Azure.Storage.Blobs", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Optional: control noise from built-in Microsoft logging
            .MinimumLevel.Debug()
            .WriteTo.DatabaseSink(featureConfig)
            .WriteTo.Console(outputTemplate: temp.ToString())
            .Enrich.FromLogContext()
            .Enrich.With<FileIt.Infrastructure.Logging.EventNameEnricher>()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", featureConfig.Application)
            .Enrich.WithProperty("ApplicationVersion", featureConfig.ApplicationVersion)
            .Enrich.WithProperty(
                "InfrastructureVersion",
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
            );
        // Skip local file sink on Azure Functions (read-only filesystem at /home/site/wwwroot).
        // App Insights handles cloud logging via the RELEASE block below.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
        {
            // Rich rolling log file for dev/QA/business sharing (#43).
            // One file per host (derived from Application name), rolling daily,
            // 30-day retention, 100MB per-file cap.
            //
            // Log output folder resolution:
            // 1. LOG_OUTPUT_DIR env var wins (production flexibility - points at Azure Files, mounted volume, etc.)
            // 2. Otherwise, walk up from the current directory to find the solution root and drop logs/ next to it
            //    (local dev - all 3 hosts converge on <repo_root>/logs/ so devs and QA find them in one place)
            // 3. Otherwise, current directory as a last-resort fallback
            var logFolder = Environment.GetEnvironmentVariable("LOG_OUTPUT_DIR");
            if (string.IsNullOrWhiteSpace(logFolder))
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null && !dir.GetFiles("*.sln").Any())
                {
                    dir = dir.Parent;
                }
                logFolder = dir != null
                    ? Path.Combine(dir.FullName, "logs")
                    : Path.Combine(Directory.GetCurrentDirectory(), "logs");
            }
            Directory.CreateDirectory(logFolder);

            var hostName = (featureConfig.Application ?? "fileit")
                .Replace("FileIt.Module.", string.Empty)
                .Replace("FileIt.", string.Empty)
                .ToLowerInvariant();

            var sharedLogPath = Path.Combine(logFolder, $"{hostName}-.log");

            var sharedOutputTemplate =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} | {Level:u3} | {Application,-40} | " +
                "Correlation: {CorrelationId,-36} | Invocation: {InvocationId,-36} | " +
                "Event {EventName,-40} | {SourceContext} | {Message:lj}{NewLine}{Exception}";

            // Delete stale log files so the new template applies cleanly on the next run
            try
            {
                foreach (var stale in Directory.EnumerateFiles(logFolder, $"{hostName}-*.log"))
                {
                    File.Delete(stale);
                }
            }
            catch { /* best effort, ignore if files are locked */ }

            loggerConfig.WriteTo.File(
                path: sharedLogPath,
                outputTemplate: sharedOutputTemplate,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 100_000_000,
                rollOnFileSizeLimit: true,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2)
            );
        }

        // Ship logs to Aspire dashboard via OTLP when running under Aspire.
        // OTEL_EXPORTER_OTLP_ENDPOINT is auto-injected by Aspire into each child process.
        // If the variable is absent (standalone func run), this block is skipped and behavior is unchanged.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // Enable Serilog SelfLog to stderr so we can see sink errors in the Aspire console
            Serilog.Debugging.SelfLog.Enable(Console.Error);

            // Aspire ships OTLP over https with a dev cert on localhost.
            // Bypass cert validation on the underlying HttpClient for local dev.
            var httpHandler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            loggerConfig.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlpEndpoint;
                options.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                options.HttpMessageHandler = httpHandler;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = featureConfig.Application ?? "FileIt",
                    ["service.version"] = featureConfig.ApplicationVersion ?? "1.0.0"
                };
            });
        }

#if RELEASE
        loggerConfig.WriteTo.ApplicationInsights(
            featureConfig.AppInsightsConnectionString,
            TelemetryConverter.Traces
        );
#endif

        Log.Logger = loggerConfig.CreateLogger();

        // Register your custom provider
        builder.AddSerilog(Log.Logger, true);

        // Use the preconfigured Serilog Logger (Log.Logger) for the Functions application.
        // FunctionsApplicationBuilder doesn't expose Host, so attach Serilog through the ILoggingBuilder.
        if (!string.IsNullOrWhiteSpace(selfLogFilePath))
        {
            Serilog.Debugging.SelfLog.Enable(msg => File.AppendAllText(selfLogFilePath, msg));
        }
        return builder;
    }
}
