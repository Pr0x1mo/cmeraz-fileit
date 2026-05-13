# Application Insights queries

KQL queries used to observe FileIt running in Azure. Each file is runnable as-is against the lab-35 Application Insights workspace (`appinsights-lab35-b5a4884856bc8d`). Substitute `cloud_RoleName` values for other environments.

| File | Purpose |
| --- | --- |
| `01-role-activity.kql` | Per-FA activity counts over a window. First query to run when checking whether telemetry is flowing. |
| `02-dataflow-happy-path.kql` | Walks one blob from EventGrid trigger through Watcher, Subscriber, transform, and final upload. |
| `03-simple-services-complex-chain.kql` | The cross-FA chain: SimpleWatcher -> services.ApiAdd -> complex.Documents_Create -> SimpleSubscriber. |
| `04-dlq-pipeline.kql` | The 11 retries, classifier decision, and DeadLetterRecord persistence after a poison message. |

Companion to `docs/queries/commonlog/` which queries the same flows from the database sink.