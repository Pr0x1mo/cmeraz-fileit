# Lab-35 deploy runbook (closes #2)

Deploy the four FileIt Function Apps to Azure Lab-35 from a developer laptop, when the SCM site sits behind a private endpoint and corp DNS will not resolve it.

## Prerequisites

- `az login` against the lab-35 subscription (`cda94130-7f9a-4ff5-9211-3cf96bb6086e`)
- Website Contributor + Storage Blob Data Contributor on each `rg-fileit35-*` resource group
- The four user-assigned managed identities exist and are attached to their function apps: `mi-fileit35-dataflow`, `mi-fileit35-services`, `mi-fileit35-simple`, `mi-fileit35-complex`
- The MIs hold Azure Service Bus Data Owner on `sbus-pe-2d99722c9843d8`, Storage Blob Data Contributor on their per-module storage account, and a SQL user on `jmplabsv04/FileIt`
- App Insights `appinsights-lab35-b5a4884856bc8d` is wired into each FA via `APPLICATIONINSIGHTS_CONNECTION_STRING`

## Step 1: build + publish each host

```powershell
cd <repo-root>
dotnet build FileIt.sln -c Release
```

The build produces a deployable zip at `<Host>\bin\Release\net10.0\*.zip`. Use that, NOT a manually compressed archive. Compress-Archive on Windows writes path separators with backslashes, which crashes Kudu sync on the Linux FA: `EINVAL invalid argument, open '/home/site/wwwroot/.azurefunctions\Azure.Core.Amqp.dll'`.

## Step 2: bypass corp DNS to reach SCM

Each function app sits behind a private endpoint targeting `sites` (main). The SCM hostname `*.scm.azurewebsites.net` resolves through the same privatelink DNS zone in corp DNS, so it returns an unreachable private IP from a developer laptop. Resolve the public IP through a public DNS server and add a hosts file entry:

```powershell
nslookup fa-fileit35-dataflow-e0d009791e61eb.scm.azurewebsites.net 8.8.8.8
# In Lab-35 today the four FAs share the same App Service plan and return 52.228.84.33.
```

Run PowerShell as Administrator:

```powershell
$entries = @"
52.228.84.33 fa-fileit35-dataflow-e0d009791e61eb.scm.azurewebsites.net
52.228.84.33 fa-fileit35-services-d7380327c31639.scm.azurewebsites.net
52.228.84.33 fa-fileit35-simple-ddcb3072565673.scm.azurewebsites.net
52.228.84.33 fa-fileit35-complex-934e0557fe5ae8.scm.azurewebsites.net
"@
Add-Content -Path C:\Windows\System32\drivers\etc\hosts -Value "`n$entries"
ipconfig /flushdns
```

Confirm with `ping fa-fileit35-dataflow-e0d009791e61eb.scm.azurewebsites.net`. It should reply from the public IP.

## Step 3: configure every app setting BEFORE deploy

Per FA, with that FA's MI clientId, storage URI, and resource group. CRITICAL: set BOTH naming conventions for ServiceBus and Storage. The Infrastructure layer's config validator reads the plain keys (FileItServiceBus, FileItStorage), and the Azure SDK clients plus the ServiceBusTrigger binding layer read the double-underscore variants (FileItServiceBus__fullyQualifiedNamespace, ServiceBus__fullyQualifiedNamespace, FileItStorage__serviceUri). If you set only one, three of the four FAs silently fail startup with "Required configuration values are missing: FileItServiceBus, FileItStorage".

```powershell
# Delete settings that fight us
az functionapp config appsettings delete --resource-group rg-fileit35-dataflow-01 --name fa-fileit35-dataflow-e0d009791e61eb --setting-names WEBSITE_RUN_FROM_PACKAGE ENABLE_ORYX_BUILD

# Set the full config block
az functionapp config appsettings set `
  --resource-group rg-fileit35-dataflow-01 `
  --name fa-fileit35-dataflow-e0d009791e61eb `
  --settings `
    "AZURE_CLIENT_ID=<dataflow MI clientId>" `
    "SERVICEBUS_NAMESPACE=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "FileItServiceBus=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "FileItServiceBus__fullyQualifiedNamespace=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "ServiceBus__fullyQualifiedNamespace=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "FileItStorage=https://fileit35dataflowstoragea.blob.core.windows.net/" `
    "FileItStorage__serviceUri=https://fileit35dataflowstoragea.blob.core.windows.net/" `
    "FileItDbConnection=Server=tcp:jmplabsv04.database.windows.net,1433;Database=FileIt;Authentication=Active Directory Default;Encrypt=True;" `
    "SCM_DO_BUILD_DURING_DEPLOYMENT=false"
```

Repeat for services, simple, complex with their own MI clientIds and storage URIs.

## Step 4: deploy each FA

Use the legacy `az functionapp deployment source config-zip` command. The newer `az functionapp deploy --type zip` triggers an Oryx server-side build that fails on pre-compiled output with `Couldnt detect a version for the platform 'dotnet'`.

```powershell
az functionapp deployment source config-zip `
  --resource-group rg-fileit35-dataflow-01 `
  --name fa-fileit35-dataflow-e0d009791e61eb `
  --src .\FileIt.Module.DataFlow.Host\bin\Release\net10.0\FileIt.Module.DataFlow.Host.zip
```

Repeat for services, simple, complex.

## Step 5: overwrite host.json on each FA via SSH (REQUIRED EVERY DEPLOY)

Azure's deploy process server-side injects an `extensionBundle` block into host.json on every deploy. That block is for script-based languages (Python, Node) and makes the .NET isolated runtime ignore the compiled DLLs, loading zero functions even though everything else is correct.

Open each FA's Kudu site in a browser:
- https://fa-fileit35-dataflow-e0d009791e61eb.scm.azurewebsites.net
- https://fa-fileit35-services-d7380327c31639.scm.azurewebsites.net
- https://fa-fileit35-simple-ddcb3072565673.scm.azurewebsites.net
- https://fa-fileit35-complex-934e0557fe5ae8.scm.azurewebsites.net

Click SSH (App), then paste:

```bash
cat > /home/site/wwwroot/host.json << 'EOF'
{
    "version": "2.0",
    "logging": {
        "applicationInsights": {
            "samplingSettings": {
                "isEnabled": true,
                "excludedTypes": "Request"
            },
            "enableLiveMetricsFilters": true
        }
    }
}
EOF
```

## Step 6: restart each FA

```powershell
az functionapp restart --resource-group rg-fileit35-dataflow-01 --name fa-fileit35-dataflow-e0d009791e61eb
az functionapp restart --resource-group rg-fileit35-services-01 --name fa-fileit35-services-d7380327c31639
az functionapp restart --resource-group rg-fileit35-simple-01 --name fa-fileit35-simple-ddcb3072565673
az functionapp restart --resource-group rg-fileit35-complex-01 --name fa-fileit35-complex-934e0557fe5ae8
```

Wait 60 to 90 seconds per FA.

## Step 7: verify

In SSH (App) on each FA Kudu site, `tail -50 /home/LogFiles/Application/Functions/Host/*.log`. A clean startup ends with `Host lock lease acquired by instance ID '<id>'` and lists `ServiceBusOptions`, `HttpOptions`, and `ConcurrencyOptions` with no listener-startup exceptions after.

In the Azure Portal, the Functions tab for each FA should list:
- dataflow (3): DataFlowDeadLetterReader, DataFlowSubscriber, DataFlowWatcher
- services (6): ApiAdd, ApiAdd_Test_Producer, DeadLetterReplayHttp, DeadLetterReplayTimer, Health, ServicesDeadLetterReader
- simple (6): Health, HelloWorld, SimpleFlowDeadLetterReader, SimpleSubscriber, SimpleTest, SimpleWatcher
- complex (8): Documents_Create, Documents_Delete, Documents_Export, Documents_Get, Documents_List, Health, Swagger_Spec, Swagger_UI

## Why this is hour-one work, not week-one work

Total deploy time on the second run with this runbook is roughly fifteen minutes per FA, almost all of it Azure portal propagation. The first run took multiple hours over two days because the failure modes are layered: private endpoint blocks DNS, manual zip blocks Kudu sync, Oryx blocks pre-compiled output, extension bundle blocks function loading, default Storage settings expect a key not a managed identity, the Service Bus binding layer and the Infrastructure layer read different config keys for the same namespace (and BOTH must be set). Each layer has to be configured for managed identity in its own way. The runbook captures the order so the next module migration is a repeatable hour rather than another debugging session.

## Common pitfall: half-deployed FAs

The first closure session got dataflow fully working and assumed the other three were also working because the deploy returned status 4 and "Host lock lease acquired" appeared in their logs. They were not working. Portal showed an empty Functions tab and SSH logs showed `0 functions found (Custom)` because:

1. The plain `FileItServiceBus` and `FileItStorage` keys were never set, only the `__fullyQualifiedNamespace` / `__serviceUri` variants. Infrastructure config validator hard-fails on the plain key absence.
2. The host.json overwrite was applied only to dataflow.
3. WEBSITE_RUN_FROM_PACKAGE was left at 1 on the other three.

The lesson: verify every FA shows its functions in Portal AND its host log shows clean startup (no `0 functions found`). Do not declare any FA done based on the deploy command exit code alone.