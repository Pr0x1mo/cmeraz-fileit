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

$hosts = @(
  @{ proj="FileIt.Module.DataFlow.Host\FileIt.Module.DataFlow.Host.csproj"; out="dataflow" },
  @{ proj="FileIt.Module.Services\FileIt.Module.Services.Host\FileIt.Module.Services.Host.csproj"; out="services" },
  @{ proj="FileIt.Module.SimpleFlow\FileIt.Module.SimpleFlow.Host\FileIt.Module.SimpleFlow.Host.csproj"; out="simple" },
  @{ proj="FileIt.Module.Complex\FileIt.Module.Complex.Host\FileIt.Module.Complex.Host.csproj"; out="complex" }
)
foreach ($h in $hosts) {
  dotnet publish $h.proj -c Release -o ".\publish\$($h.out)"
}
```

Use the SDK-generated zip at `<Host>\bin\Release\net10.0\*.zip`. Do not manually `Compress-Archive` the publish folder; Windows path separators (backslashes) crash Kudu sync on the Linux FA with `EINVAL invalid argument` on `.azurefunctions\X.dll`.

## Step 2: bypass corp DNS to reach SCM

Each function app sits behind a private endpoint targeting `sites` (main). The SCM hostname `*.scm.azurewebsites.net` resolves through the same privatelink DNS zone in corp DNS, so it returns an unreachable private IP from a developer laptop. Resolve the public IP through a public DNS server and add a hosts file entry:

```powershell
nslookup fa-fileit35-dataflow-e0d009791e61eb.scm.azurewebsites.net 8.8.8.8
# In Lab-35 today the four FAs share the same App Service plan and return 52.228.84.33.

# Run as Administrator:
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

## Step 3: deploy each FA

Use the legacy `az functionapp deployment source config-zip` command. The newer `az functionapp deploy --type zip` triggers an Oryx server-side build that fails on pre-compiled output with `Couldnt detect a version for the platform 'dotnet'`.

```powershell
az functionapp deployment source config-zip `
  --resource-group rg-fileit35-dataflow-01 `
  --name fa-fileit35-dataflow-e0d009791e61eb `
  --src .\FileIt.Module.DataFlow.Host\bin\Release\net10.0\FileIt.Module.DataFlow.Host.zip
```

Repeat for services, simple, complex.

## Step 4: configure each FA for managed-identity auth

Each FA needs six settings to wire up Service Bus (binding + tools), Storage, SQL, and the MI clientId so `DefaultAzureCredential` picks the right identity. Per FA, with that FA's MI clientId:

```powershell
az functionapp config appsettings set `
  --resource-group rg-fileit35-dataflow-01 `
  --name fa-fileit35-dataflow-e0d009791e61eb `
  --settings `
    "AZURE_CLIENT_ID=<dataflow MI clientId>" `
    "SERVICEBUS_NAMESPACE=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "FileItServiceBus__fullyQualifiedNamespace=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "ServiceBus__fullyQualifiedNamespace=sbus-pe-2d99722c9843d8.servicebus.windows.net" `
    "FileItStorage__serviceUri=https://fileit35dataflowstoragea.blob.core.windows.net/" `
    "FileItDbConnection=Server=tcp:jmplabsv04.database.windows.net,1433;Database=FileIt;Authentication=Active Directory Default;Encrypt=True;"

az functionapp restart `
  --resource-group rg-fileit35-dataflow-01 `
  --name fa-fileit35-dataflow-e0d009791e61eb
```

The naming matters. The Infrastructure layer reads `FileItServiceBus__fullyQualifiedNamespace`, but the Functions binding layer reads `ServiceBus__fullyQualifiedNamespace` (the trigger attribute uses `Connection = "ServiceBus"`). Both must be set or one of the two layers fails. Same idea for Storage: `FileItStorage__serviceUri`, not `__blobServiceUri`.

## Step 5: post-deploy fixes that may apply

- `host.json` on the deployed FA must not contain an `extensionBundle` block. The Functions runtime treats its presence as a signal that the app is script-based and loads zero functions from the compiled DLLs.
- `WEBSITE_RUN_FROM_PACKAGE` must be unset or empty. Setting it to `1` makes the runtime expect a mounted zip rather than extracted files.
- If you see `Could not find the .azurefunctions folder in the deployed artifacts`, the deploy zip did not include the isolated worker bootstrap. Use the SDK-generated zip from Step 1.

## Step 6: verify

In SSH (App) on each FA Kudu site, `tail -50 /home/LogFiles/Application/Functions/Host/*.log`. A clean startup ends with `Host lock lease acquired by instance ID '<id>'` and lists `ServiceBusOptions`, `HttpOptions`, and `ConcurrencyOptions` with no listener-startup exceptions after.

## Why this is hour-one work, not week-one work

Total deploy time on the second run with this runbook is roughly fifteen minutes per FA. The first run took multiple hours because the failure modes are layered: private endpoint blocks DNS, manual zip blocks Kudu sync, Oryx blocks pre-compiled output, extension bundle blocks function loading, default Storage settings expect a key not a managed identity, the Service Bus binding layer and the Infrastructure layer read different config keys for the same namespace. The runbook captures the order so the next module migration is a repeatable hour.