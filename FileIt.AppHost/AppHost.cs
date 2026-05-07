using Aspire.Hosting.ApplicationModel;
using Azure.Storage.Blobs;

var builder = DistributedApplication.CreateBuilder(args);

// --- Azurite (Blob/Queue/Table) ---
var azurite = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator =>
    {
        emulator.WithBlobPort(10000);
        emulator.WithQueuePort(10001);
        emulator.WithTablePort(10002);
    });

var blobs = azurite.AddBlobs("blobs");

// --- Azure SQL (cloud) ---
var azureSql = builder.AddConnectionString("azureSql");

// --- Azure Service Bus (cloud) ---
var serviceBus = builder.AddConnectionString("serviceBus");

var complex = builder.AddProject<Projects.FileIt_Module_Complex_Host>("complex-host")
    .WithReference(blobs)
    .WithEnvironment("FileItDbConnection", azureSql)
    .WithEnvironment("FileItServiceBus", serviceBus)
    .WithEnvironment("ConnectionStrings__ServiceBus", serviceBus)
    .WaitFor(blobs);
// --- Func Apps with full wiring ---
var services = builder.AddProject<Projects.FileIt_Module_Services_Host>("services-host")
    .WithReference(blobs)
    .WithEnvironment("FileItDbConnection", azureSql)
    .WithEnvironment("FileItServiceBus", serviceBus)
    .WithEnvironment("ConnectionStrings__ServiceBus", serviceBus)
    .WithEnvironment("ConnectionStrings__ComplexApi", "http://localhost:7064")
    .WaitFor(complex)
    .WaitFor(blobs);

var simpleflow = builder.AddProject<Projects.FileIt_Module_SimpleFlow_Host>("simpleflow-host")
    .WithReference(blobs)
    .WithEnvironment("FileItDbConnection", azureSql)
    .WithEnvironment("FileItServiceBus", serviceBus)
    .WithEnvironment("ConnectionStrings__ServiceBus", serviceBus)
    .WaitFor(services)
    .WaitFor(blobs);

var dataflow = builder.AddProject<Projects.FileIt_Module_DataFlow_Host>("dataflow-host")
    .WithReference(blobs)
    .WithEnvironment("FileItDbConnection", azureSql)
    .WithEnvironment("FileItServiceBus", serviceBus)
    .WithEnvironment("ConnectionStrings__ServiceBus", serviceBus)
    .WaitFor(services)
    .WaitFor(blobs);

// --- Ensure blob containers exist once Aspire has created the resources ---
// Uses Aspire's eventing API (the modern replacement for IDistributedApplicationLifecycleHook).
// Fires after all resources are created, so no timing hack is needed.
builder.Eventing.Subscribe<AfterResourcesCreatedEvent>(async (@event, cancellationToken) =>
{
    // Well-known Azurite development connection string.
    // This is the standard local emulator key and is safe to commit to source control.
    // See: https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite
    const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;" +
        "AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    var containers = new[]
    {
        "dataflow-source", "dataflow-working", "dataflow-final",
        "simple-source", "simple-working", "simple-final"
    };

    var serviceClient = new BlobServiceClient(AzuriteConnectionString);
    var failedContainers = new List<string>();

    foreach (var containerName in containers)
    {
        // Retry briefly since Azurite may not accept connections the instant the resource reports created
        bool succeeded = false;
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await serviceClient
                    .GetBlobContainerClient(containerName)
                    .CreateIfNotExistsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine($"[container-init] ensured: {containerName}");
                succeeded = true;
                break;
            }
            catch when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[container-init] FAILED for {containerName} after {attempt} attempts: {ex.Message}");
                failedContainers.Add(containerName);
            }
        }
    }

    // If any containers failed to initialize, throw to prevent the application from starting
    // in an inconsistent state. This is better than silent failure.
    if (failedContainers.Count > 0)
    {
        throw new InvalidOperationException(
            $"Failed to initialize {failedContainers.Count} blob container(s): {string.Join(", ", failedContainers)}. " +
            "Ensure Azurite is running and accessible.");
    }
});

builder.Build().Run();
