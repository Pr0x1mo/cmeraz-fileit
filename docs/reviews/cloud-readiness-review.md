# Cloud Readiness Review

Closes issue #18.

This review assesses what FileIt looks like today in Lab-35, and what would need to change to host the same patterns on a production CIBC subscription with the network, availability, and security posture that the bank requires.

It is organized around the five questions raised in the original issue: Function App tier, ASE, VNET, Service Bus tier, and database tier.

## Lab-35 baseline

The system runs end-to-end in Lab-35 as of 2026-05-13. All four Function Apps are deployed, all four respond to triggers, and the happy and dead-letter paths have been observed in real cloud telemetry. Evidence is preserved in App Insights as the four KQL queries committed under `docs/queries/appinsights/` and in the runbook at `docs/runbooks/lab35-eventgrid-wiring.md`.

Component summary as deployed:

| Layer | Lab-35 today |
| --- | --- |
| Function Apps | Elastic Premium (EP), four apps, one per module |
| Service Bus | Premium tier, single namespace `sbus-pe-2d99722c9843d8`, Canada Central |
| Database | Azure SQL on `jmplabsv04.database.windows.net`, lab tier (single database) |
| Storage | One V2 storage account per module, RA-GRS not required for lab |
| App Insights | Workspace-based, single workspace `appinsights-lab35-b5a4884856bc8d` |
| Key Vault | `kv-labb18df5017d42`, RBAC mode, secrets reachable via managed identity |
| Identity | User-assigned managed identity per Function App, passwordless to SB, Storage, SQL |
| Network | Public ingress, SCM private endpoint, no VNET integration |

The system functions correctly under this baseline. The questions that follow are about the additional posture required for production, not about correctness.

## Function App tier

FileIt currently runs on Elastic Premium. EP is the right tier for FileIt's workload shape for three reasons.

First, the dataflow and simple modules are blob-triggered through EventGrid, and the services and complex modules are HTTP-triggered. EP handles both trigger families on warm instances, which removes the cold-start latency that the Consumption tier introduces on HTTP and which makes Service Bus binding behaviour more predictable.

Second, EP supports VNET integration. The Consumption tier does not. Production FileIt needs to reach Service Bus, Storage, SQL, and Key Vault over private endpoints, which requires the FA to be VNET-joined.

Third, EP scales horizontally up to 100 instances with pre-warmed pools, which matches the variability expected from a FileHandler-replacement workload (long quiet stretches punctuated by batch arrivals).

The alternative is the Flex Consumption tier, which is GA in Canada Central and supports VNET integration as of late 2025. Flex offers per-second billing rather than per-instance billing, which is cheaper for workloads that idle more than they run. Flex has stricter constraints on host customization, durable functions, and certain trigger types. A migration from EP to Flex is reasonable as a future optimization but is not a precondition for going to production. Recommendation: stay on EP for the first production deploy, evaluate Flex after the first quarter of operating data.

ASE (App Service Environment) is a third option and is addressed separately below.

## ASE

ASE v3 is a single-tenant App Service deployment that runs inside a customer-owned VNET. The FAs are not shared with other tenants on the underlying hardware. ASE v3 is the option for workloads that need full network isolation, custom inbound IP allowlists at the platform layer, or compliance requirements that forbid multi-tenant hosting.

ASE is significantly more expensive than EP. The base cost is roughly an order of magnitude higher per month before any plan instances are added, because the ASE itself has a fixed footprint regardless of utilization. For FileIt's expected throughput (hundreds to low thousands of files per day across all Providers combined once the migration is complete), the multi-tenant EP tier with VNET integration delivers equivalent network posture at a fraction of the cost.

Recommendation: do not provision an ASE for FileIt. EP plus VNET integration plus private endpoints on the dependencies achieves the same effective network isolation for production traffic. Reserve ASE for workloads that have a documented compliance requirement that cannot be satisfied otherwise.

## VNET integration

This is the largest single network change required between Lab-35 and production.

In Lab-35, the FAs ingress and egress over the public Azure backbone. SB, Storage, SQL, and KV are all reachable over their public endpoints. The SCM private endpoint exists but only protects the deployment surface, not the runtime data plane. The ComplexApi outbound HTTP call from services to complex goes out the FA's public egress and back in through complex's public ingress.

In production, every dependency the FA reaches needs to be addressable over a private endpoint, and every dependency that exposes a public endpoint needs to have public access disabled. The required topology:

- One VNET per environment with three subnets: function-integration (delegated to Microsoft.Web/serverFarms), private-endpoints, and management.
- Regional VNET integration enabled on each Function App, pointing at the function-integration subnet.
- Private endpoints in the private-endpoints subnet for the Service Bus namespace, each storage account, the SQL server, the Key Vault, and the App Insights ingestion endpoint.
- Private DNS zones for each Azure service, linked to the VNET, so the FAs resolve the dependency hostnames to private IPs.
- Public network access disabled on SB, storage, SQL, KV, and App Insights, with the FA's private endpoints whitelisted.
- Outbound HTTP from services to complex routed inside the VNET, not over the public backbone.

This is a non-trivial topology change. The lab took shortcuts that production cannot. Closing this gap is the largest single workstream in the production-readiness backlog and is the right place to start a dedicated cloud-readiness Bicep project under `infrastructure/`.

## Service Bus tier

Service Bus is already on Premium in Lab-35 and Premium is the right answer for production for three reasons.

First, Premium supports private endpoints. Standard does not. Production FileIt cannot use Standard.

Second, Premium gives dedicated messaging units rather than shared multi-tenant capacity, which removes the noisy-neighbor risk on a system where dead-letter classification depends on observable retry timing.

Third, Premium supports geo-disaster-recovery pairing, which is the right mechanism for surviving a regional outage on the messaging layer without manual failover of every consumer.

Operational considerations for production:

- Size the namespace at one messaging unit per environment to start. Scale up based on observed depth and throughput rather than guessed peak.
- Enable geo-DR pairing between Canada Central and Canada East. Document the failover procedure in `docs/runbooks/`.
- Provision queues and topics through Bicep with idempotent definitions, not through the portal.
- Set message TTL explicitly per queue based on the business contract for that flow rather than relying on the namespace default.
- Configure max delivery count per queue. The current default of eleven is the right number for FileIt's classifier logic; do not change it without updating the classifier rule that depends on it.

## Database tier

The lab runs on a single Azure SQL database on `jmplabsv04`, lab-grade tier. Production needs more.

The right production target for FileIt is Azure SQL Managed Instance Business Critical with zone redundancy, in the same primary region as the Function Apps, with a geo-replicated failover group to a secondary region. The justification:

- The database is on the hot path for every message. CommonLog writes happen on every function invocation. DeadLetterRecord writes happen on every failure. ApiLog writes happen on every API call. A throughput-limited or latency-spiky database tier shows up immediately as Service Bus consumer lag.
- Business Critical uses local SSD with synchronous replication across availability zones in the primary region, which gives both low write latency and zonal redundancy in one tier.
- A geo-replicated failover group with automatic failover policy survives a regional outage without manual intervention. The recovery point objective is single-digit seconds and the recovery time objective is single-digit minutes.
- MI rather than Azure SQL Database because MI supports private endpoints natively, supports SQL Agent for scheduled work (a relevant capability when migrating Providers that currently use SQL Agent), and supports cross-database queries within the instance without requiring the deprecated linked-server pattern that FileIt is replacing.

Expected performance against MI Business Critical for FileIt's workload shape (per-message audit writes, occasional dead-letter inserts, occasional batch queries from the operator UI in #7): under 5ms p99 for the single-row inserts, under 50ms p99 for the operator queries, with headroom for an order of magnitude growth in throughput before requiring tier escalation.

Cost: MI Business Critical 4 vCore in Canada Central is roughly $4000 per month per instance at retail. Reserved-capacity pricing knocks this down meaningfully. The lab today runs on a sub-$50 per month tier. The order-of-magnitude jump is real and needs explicit budget approval at the gate where lab-35 transitions to a CIBC production subscription.

If MI Business Critical is out of budget for the initial production deploy, the fallback is Azure SQL Database Business Critical at the General Purpose serverless tier with auto-pause, which gives most of the same properties at a much lower idle cost. This is a downgrade only on the SQL-Agent and linked-server-replacement capabilities, both of which are forward-looking concerns rather than day-one requirements for FileIt itself.

Recommendation: target MI Business Critical for the production posture, accept SQL Database Business Critical serverless as the cost-reduced alternative if the MI budget is contested.

## Other items raised by evidence in Lab-35

These are not from the original issue text but surfaced during cloud verification and belong in the same review.

### ComplexApi connection string

The services FA inherits `ConnectionStrings__ComplexApi=http://localhost:7064` from local Aspire orchestration. Without environment-specific override, every ApiAdd invocation fails in ~20ms in cloud and lands in the DLQ as Poison. The fix is to set the connection string per environment to the complex FA's hostname. The runbook at `docs/runbooks/lab35-eventgrid-wiring.md` documents this; the production deploy should treat this as a required app setting alongside the SB, storage, and SQL references.

A stronger fix is to switch to service discovery via Aspire's published endpoints rather than hardcoded localhost. That change belongs on the future-work list and does not block production.

### Function App SCM private endpoint and deployment

The Lab-35 deploy required eight workarounds documented in `docs/runbooks/lab35-deploy-runbook.md`, several of which arose from the SCM private endpoint design. Production deploys should use a deploy agent that lives inside the VNET (for example, a self-hosted GitHub Actions runner on an Azure VM joined to the same VNET, or Azure DevOps with a self-hosted agent), which removes the hosts-file workaround and the public-IP discovery step. This is a CI/CD design decision that is out of scope for this review but is the natural next backlog item after the VNET topology is provisioned.

### Storage account redundancy

Each module storage in Lab-35 is provisioned at the default LRS tier. Production should be ZRS at minimum, with the dataflow-final and simple-final containers replicated via GRS or a separate copy job into a secondary region. The exact redundancy choice is a per-Provider business call: some FileHandler Providers route through Storage as a durable hand-off, others use it as transient staging. The redundancy posture should follow the business-criticality of the data passing through each container, not be set globally.

### Application Insights workspace and sampling

Lab-35 emits unsampled telemetry, which is fine for verification work but would be expensive at production volumes. The production posture should set the App Insights daily cap, enable adaptive sampling at the SDK level, and route to a dedicated Log Analytics workspace per environment. The four KQL queries in `docs/queries/appinsights/` continue to work under adaptive sampling because they query traces by role name and by message content rather than by exact counts.

## Summary of gating items for production

In priority order, the items that must be resolved before FileIt can take real Provider traffic in production:

1. VNET topology and private endpoint provisioning for SB, Storage, SQL, KV, App Insights.
2. Database tier upgrade to MI Business Critical (or SQL Database Business Critical serverless if MI budget is contested).
3. Production-grade deploy pipeline using a VNET-resident agent.
4. Environment-specific app settings management, including the ComplexApi connection string fix.
5. Service Bus geo-DR pairing and documented failover procedure.
6. App Insights sampling and daily cap configuration per environment.
7. Storage redundancy posture per container based on per-Provider business criticality.

Each of these is a discrete piece of work with a known shape. None of them block one another in a way that would force a serial schedule. The right sequence is to start with item 1 (VNET) as the foundation, then 2 (DB) in parallel, then 3-7 as the production-deploy backlog.

## What did not need to change

Worth listing explicitly, the patterns proven in Lab-35 that transfer to production without modification:

- Managed identity authentication to SB, Storage, SQL. No connection strings with secrets in any environment.
- The four-FA module boundary. Each FA has its own MI, its own storage, its own deployment lifecycle. Adding a new Provider FA is an additive change.
- EventGrid system topics on blob storage routing to Watcher functions. The same wiring runbook applies to production with private endpoints substituted in.
- The DLQ classifier and ingestion pipeline. Tonight's cloud run produced DeadLetterRecord 74 with the same classification and lifecycle behaviour observed in local Aspire runs.
- The CommonLog database sink. The Serilog enrichment, EventName resolution, and operator query patterns are unaffected by deployment topology.
- The architecture tests. They run at build time and have no runtime dependency on cloud topology.

The proof that these transfer is in the telemetry captured under `docs/queries/appinsights/` and in the issue closures referenced from the FileIt story document.
