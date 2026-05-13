# Lab-35 EventGrid wiring

Companion to `lab35-deploy-runbook.md`. After all 4 FAs are deployed, blob events still need to be routed to the Watcher functions. Only `dataflow` and `simple` need this; `services` and `complex` have no blob triggers.

## Prereqs

- `EventGrid Contributor` on each `rg-fileit35-{module}-01`
- Storage accounts exist: `fileit35{module}storagea`
- FAs running with managed identity
- Containers `{module}-source`, `{module}-working`, `{module}-final` exist on each storage

## Commands

```powershell
$sub = az account show --query id -o tsv

az eventgrid system-topic create -g rg-fileit35-dataflow-01 -n egst-fileit35-dataflow -l canadacentral --topic-type Microsoft.Storage.StorageAccounts --source /subscriptions/$sub/resourceGroups/rg-fileit35-dataflow-01/providers/Microsoft.Storage/storageAccounts/fileit35dataflowstoragea

az eventgrid system-topic create -g rg-fileit35-simple-01 -n egst-fileit35-simple -l canadacentral --topic-type Microsoft.Storage.StorageAccounts --source /subscriptions/$sub/resourceGroups/rg-fileit35-simple-01/providers/Microsoft.Storage/storageAccounts/fileit35simplestoragea

az eventgrid system-topic event-subscription create -n sub-dataflow-source -g rg-fileit35-dataflow-01 --system-topic-name egst-fileit35-dataflow --endpoint-type azurefunction --endpoint /subscriptions/$sub/resourceGroups/rg-fileit35-dataflow-01/providers/Microsoft.Web/sites/<dataflow-fa>/functions/DataFlowWatcher --included-event-types Microsoft.Storage.BlobCreated --subject-begins-with /blobServices/default/containers/dataflow-source/

az eventgrid system-topic event-subscription create -n sub-simple-source -g rg-fileit35-simple-01 --system-topic-name egst-fileit35-simple --endpoint-type azurefunction --endpoint /subscriptions/$sub/resourceGroups/rg-fileit35-simple-01/providers/Microsoft.Web/sites/<simple-fa>/functions/SimpleWatcher --included-event-types Microsoft.Storage.BlobCreated --subject-begins-with /blobServices/default/containers/simple-source/
```

## Gotcha: ComplexApi localhost

Services FA inherits `ConnectionStrings__ComplexApi=http://localhost:7064` from local dev. In cloud this has to point at the Complex FA hostname or every ApiAdd invocation fails in ~20ms and lands in DLQ as Poison after 11 retries.

```powershell
az functionapp config appsettings set -n <services-fa> -g rg-fileit35-services-01 --settings "ConnectionStrings__ComplexApi=https://<complex-fa>.azurewebsites.net"
az functionapp restart -n <services-fa> -g rg-fileit35-services-01
```

## Evidence captured against lab-35

- DataFlowWatcher fires on `GLAccount.csv` upload, transform completes in 3083ms, `summary_GLAccount.csv` lands in `dataflow-final`
- SimpleWatcher -> ApiAdd -> Documents_Create -> SimpleSubscriber chain completes in ~10s end to end
- Poisoned `GLAccount-poison.csv` exhausts 11 retries, `DataFlowDeadLetterReader` ingests, `DeadLetterRecord 74` written with `FailureCategory=Poison` via `BuiltInReason_MaxDeliveryCountExceeded`
- Services DLQ also captured `DeadLetterRecord 59` from the pre-fix localhost outage; same classifier rule fired

See `docs/queries/appinsights/` for the KQL used to prove each path.