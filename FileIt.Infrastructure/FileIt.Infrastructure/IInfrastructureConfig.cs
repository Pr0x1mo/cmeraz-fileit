using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileIt.Infrastructure;

public interface IInfrastructureConfig
{
    string? AppInsightsConnectionString { get; set; }
    string? BusNamespace { get; set; }
    string? DbConnectionString { get; set; }
    string? Environment { get; set; }
    string? Feature { get; set; }
    string? BlobConnectionString { get; set; }
    string? BusConnectionString { get; set; }
}

public class InfrastructureConfig : IInfrastructureConfig
{
    private IConfiguration? configuration;
    public InfrastructureConfig() { }

    private const string SERVICEBUS_NAMESPACE = "SERVICEBUS_NAMESPACE";
    private const string DB_CONNECTION_STRING = "FileItDbConnection";
    private const string SERVICEBUS_CONNECTION_STRING = "FileItServiceBus";
    private const string STORAGE_CONNECTION_STRING = "FileItStorage";
    private const string APPLICATIONINSIGHTS_CONNECTION_STRING =
        "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public InfrastructureConfig(IConfiguration configuration)
    {
        this.configuration = configuration;
        List<string> missing = new List<string>();
        string? parsedValue;

        // Required for every module: database connection. Without it, the platform's
        // structured logging and audit tables don't work.
        if (ParseConfigValue(DB_CONNECTION_STRING, out parsedValue))
            DbConnectionString = parsedValue;
        else
            missing.Add(DB_CONNECTION_STRING);

        // Optional per module: Service Bus and Storage. The UI module is read-only
        // (queries SQL, uploads blobs via managed identity) and does not consume from
        // Service Bus. Each module that needs these binds them at the function level
        // and the runtime will throw at trigger registration time if they're missing.
        if (ParseConfigValue(SERVICEBUS_NAMESPACE, out parsedValue))
            BusNamespace = parsedValue;
        if (ParseConfigValue(SERVICEBUS_CONNECTION_STRING, out parsedValue))
            BusConnectionString = parsedValue;
        if (ParseConfigValue(STORAGE_CONNECTION_STRING, out parsedValue))
            BlobConnectionString = parsedValue;
        if (ParseConfigValue(APPLICATIONINSIGHTS_CONNECTION_STRING, out parsedValue))
            AppInsightsConnectionString = parsedValue;

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Required configuration values are missing: {string.Join(", ", missing)}. " +
                "Ensure these are set in appsettings.json, environment variables, or user secrets.");
        }
    }

    private bool ParseConfigValue(string key, out string? parsedValue)
    {
        parsedValue = configuration!.GetValue<string>(key);
        return !string.IsNullOrWhiteSpace(parsedValue);
    }

    public string? AppInsightsConnectionString { get; set; }
    public string? BusNamespace { get; set; }
    public string? DbConnectionString { get; set; }
    public string? Environment { get; set; }
    public string? Feature { get; set; }
    public string? BlobConnectionString { get; set; }
    public string? BusConnectionString { get; set; }
}