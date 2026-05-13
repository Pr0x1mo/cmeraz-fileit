# Testability Readiness Review

Closes issue #27.

The original issue asked for an evaluation of how open this system is to being tested and to having that testing automated. This review evaluates that, organized around the layers that exist in the codebase and the test categories that apply to each.

The conclusion up front: FileIt is unusually testable for a project of its size. Most of that testability is structural rather than incidental. The architecture tests added in #11, the dependency direction enforced by those tests, the EF in-memory provider used by the repository tests, the HttpMessageHandler mocking pattern in the HTTP clients, and the Moq-Callback pattern in the messaging tools all combine to mean that every production class has a clear seam where a test can replace its dependencies. The result is 188 unit tests plus 16 architecture tests, all green, running in roughly three seconds on a developer machine.

What follows is the evidence behind that conclusion, plus the gaps that should be filled before declaring the system test-complete for production.

## Test inventory

Counts as of 2026-05-13:

| Project | Tests | Notes |
| --- | --- | --- |
| FileIt.Architecture.Test | 16 | NetArchTest rules, build-gate |
| FileIt.Infrastructure.Test | 172 | Tools, classification, DLQ, HTTP clients, repos, logging |
| FileIt.Module.Services.App.Test | 6 | ApiAddCommand coverage |
| FileIt.Module.SimpleFlow.App.Test | 10 | BasicApiAddHandler + WatchInbound |
| FileIt.Module.DataFlow.Test | (existing scaffold) | Pre-existing happy-path tests |
| FileIt.Infrastructure.Integration | 4 | Live against jmplabsv04 / FileIt as of #16 |

Total: 188 unit tests + 16 architecture tests + 4 integration tests. All passing. All run in CI on commit.

## What makes each layer testable

### Domain

`FileIt.Domain` is pure C#. Zero project dependencies. Zero Azure SDK references. Zero EF references. Enforced by NetArchTest rule.

A domain entity is testable by constructing it directly and asserting on its computed properties. There is no test fixture to set up, no in-memory database to spin, no mock to wire. This is the easiest layer to test, and the tests that exist on it run in microseconds.

The structural enforcement matters because in the FileHandler model the equivalent layer (the per-Provider domain types) is contaminated with EF DbContext access, configuration reads, and Provider-specific concerns. Testing one of those types in isolation requires either a live database or a heavy mocking ritual. The FileIt domain layer makes that kind of contamination a build break.

### Infrastructure

`FileIt.Infrastructure` is where the building blocks live: Service Bus tools, blob tools, HTTP clients, repositories, the dead-letter classifier, and the Serilog database sink. This is the most-tested layer with 172 unit tests.

The patterns that make it testable:

- **Service Bus interactions via Moq Callback.** The production code constructs a `ServiceBusMessage` and hands it to a sender. The test mocks the sender, captures the constructed message via Moq's `Callback` mechanism, and asserts on its fields directly. This is what `TestBusTool` does. The earlier version of this test compared two distinct message instances by object identity (which is always false), so the test was structurally broken and always green. Issue #6 rewrote it with the Callback pattern, which produces a real assertion that breaks when the production code stops setting a field correctly.

- **Blob storage interactions via mocked clients.** `BlobTool` is tested against a mocked `BlobServiceClient`. The same Callback pattern captures the constructed `BlobBaseClient` and asserts on the move semantics. One production-code change was required during the test rewrite: `MoveAsync` now passes `options: null` explicitly to `StartCopyFromUriAsync` because the SDK's overload resolution does not bind cleanly to Moq with only the cancellation token named. That is exactly the kind of refactor that tests catch.

- **HTTP clients via HttpMessageHandler mocks.** `ComplexApiClient` is tested by injecting a mocked `HttpMessageHandler` into the `HttpClient` it uses. The mock returns scripted responses: happy path, 503 with Retry-After, 500, 422, cancellation. Each of these scenarios is one test. The production class never knows it is talking to a mock. Fifteen tests live on this seam.

- **Repositories via EF in-memory provider.** Each of the six repository classes (`ApiLogRepo`, `SimpleRequestLogRepo`, `DataFlowRequestLogRepo`, `ComplexDocumentRepo`, `ComplexIdempotencyRepo`, `DeadLetterRecordRepo`, `CommonLogRepo`) is tested against an in-memory EF context constructed by a shared `InMemoryCommonDbContextFactory`. Tests are isolated per scenario because each test gets its own context instance. Sixty tests live on this seam. The in-memory provider is not a perfect fidelity simulation of SQL Server (it does not enforce check constraints the same way, it serializes operations differently), but it is the right tool for testing repository logic that is not provider-specific.

- **Classifier as pure function.** The dead-letter classifier is a pure function from an envelope to a `(FailureCategory, RuleMatched, Reason)` triple. Twenty-two tests cover every branch of the five-tier rule system plus the catch-all. This is the cheapest possible thing to test, and the tests document the priority order explicitly: explicit hint property beats poison marker property beats poison payload prefix beats built-in reason beats heuristic patterns beats catch-all.

The infrastructure layer is the most-tested layer because it is also the layer where the highest concentration of business-critical decisions live. Idempotency, classification, retry, audit. Each of those decisions is observable through a unit test, which means each decision is also reviewable in version control as the test diff.

### App layer

Each module has an `App` project that holds the per-module commands and handlers. The App layer is testable through the same patterns as Infrastructure: dependencies are constructor-injected, mocks substitute the production wiring, and the unit test asserts on the observable behaviour.

The architecture rule that makes the App layer testable: App projects can reference `Domain` only. They cannot reference Infrastructure directly, and they cannot reference other modules' App projects. This means the unit test for `ApiAddCommand` does not need to spin up a database, a Service Bus client, or a blob client. It needs an `IApiLogRepo` (mocked), an `IComplexApiClient` (mocked), and an `ILogger` (Moq's standard logger mock). Six tests cover the full behaviour of `ApiAddCommand`. Ten tests cover `BasicApiAddHandler` and `WatchInbound` in SimpleFlow.

The App layer is where future Provider migrations will introduce business logic. The testability of that logic depends on the same patterns being followed. Architecture rule enforcement is what keeps this property true as the codebase grows.

### Host layer

The Function host layer is the thin wrapper around the App layer. Each Function method receives a trigger payload, constructs the command DTO, invokes the App layer, and returns. Testing this layer through unit tests is possible but is mostly an integration concern: the value of testing a Function method in isolation is low because the meaningful behaviour lives one layer deeper.

The right strategy for Host testing is to keep the Host layer thin (architecture rule: no business logic in Host) and to cover the actual behaviour at the App layer below it. This is what FileIt does.

### Architecture tests

The 16 NetArchTest rules in `FileIt.Architecture.Test` are the layer that protects all the testability properties above. Sample rules and what they protect:

- Domain has zero dependencies on Infrastructure or Modules and stays POCO. Protects: Domain layer testability.
- Infrastructure does not reference any Module or Host. Protects: Infrastructure reusability.
- App projects reference Domain only, never each other or Hosts. Protects: App layer isolation, prevents cross-module entanglement.
- No Azure SDK or EF in the App layer. Protects: App layer mockability.
- No async void outside event handlers. Protects: exception observability.
- Public types live under their declared module namespace. Protects: discoverability and the assumption that namespace == module boundary.
- Public interfaces start with I. Protects: convention.

When the rules were first introduced, three failed. Two were rule-precision issues. The third caught a real namespace bug in five SimpleFlow.Host classes that had been wrong for weeks without anyone noticing. That class of bug is exactly the cost the rules amortize.

The rules run as part of `dotnet test` and gate the build the same way unit tests do. There is no separate workflow.

### Integration tests

`FileIt.Infrastructure.Integration` exists and now works, as of #16. The four tests run against the real Azure SQL database on `jmplabsv04` with credentials supplied through the `FileItDbConnection` environment variable at user scope.

Integration tests serve a different purpose than unit tests. They catch the things the unit tests cannot:

- SQL behaviour that the EF in-memory provider does not simulate (check constraint enforcement, index hint behaviour, real query plans).
- Connection string layering bugs.
- Schema drift between the DACPAC and the running database.
- Serilog enrichment configuration that produces incorrect output (bug 7.8 in the story doc is exactly this category, filed but not yet fixed).

The integration test project is now in a state where future tests can be added without re-discovering the wiring. The earlier broken state (the "DbConnectionString missing" failure documented in #16) is fixed by a normalized config-reading convention that matches `TestHost`. The dead-code `BaseTest` class is removed. The catch-and-continue block that was masking bug 7.7 (a column-name mismatch in `DatabaseSinkTest`) is removed.

### End-to-end cloud verification

As of 2026-05-13, FileIt has been verified end-to-end against real cloud infrastructure in Lab-35. The verification covers all four Function Apps and both the happy path and the dead-letter path.

Happy paths verified:

- DataFlow: `GLAccount.csv` dropped into `dataflow-source`. EventGrid triggers `DataFlowWatcher`. Watcher moves blob to `dataflow-working` and publishes to `dataflow-transform`. `DataFlowSubscriber` consumes, runs the transform, writes `summary_GLAccount.csv` to `dataflow-final`. Total elapsed: 3083ms.
- Simple to Services to Complex chain: `simple-test2.txt` dropped into `simple-source`. `SimpleWatcher` publishes to `api-add` queue. `services.ApiAdd` consumes, calls `complex.Documents_Create` via HTTP, writes the audit row. `SimpleSubscriber` (subscribed to `api-add-simple-sub` topic) consumes the broadcast, moves the blob to `simple-final`. Total elapsed: ~10s.

Dead-letter paths verified:

- DataFlow poison: `GLAccount-poison.csv` (containing `POISON_` prefix) dropped into `dataflow-source`. Watcher publishes to `dataflow-transform`. Subscriber retries the transform 11 times, each failing. Service Bus dead-letters the message. `DataFlowDeadLetterReader` ingests, classifier returns Poison via `BuiltInReason_MaxDeliveryCountExceeded`, `DeadLetterRecord 74` written. Total elapsed: 2 seconds from blob upload to DLQ row.
- Services dead-letter (incidental): the pre-fix outage caused by the localhost ComplexApi connection string produced `DeadLetterRecord 59` with the same classification, demonstrating that the classifier rule fires correctly across modules.

The KQL queries that drove this verification are committed to `docs/queries/appinsights/` and the wiring runbook is at `docs/runbooks/lab35-eventgrid-wiring.md`.

## Test automation in CI

The unit tests, architecture tests, and integration tests run together on `dotnet test`. The integration tests are gated by the presence of the `FileItDbConnection` environment variable; in environments where the variable is absent, the integration tests skip rather than fail.

A CI pipeline running the full test suite on every push produces:

- Architecture rule violations as build failures (16 rules, 16 currently passing).
- Unit test failures as test failures (188 tests, 188 currently passing).
- Integration test failures when the DB target is reachable but the schema or wiring is broken (4 tests, 4 currently passing).
- A coverage report from the unit and integration tests for the layers that have tests.

The total runtime is dominated by the integration tests (a few seconds against the live SQL endpoint). Unit tests and architecture tests complete in under three seconds in aggregate. This is fast enough that running the full test suite as a pre-commit hook is reasonable.

## What does not yet have automated testing

These are gaps, named honestly. Each one is a known piece of future work rather than a discovered surprise.

### No load or performance tests

There is no automated load test today. The system has been exercised by ad-hoc message generation but not by a sustained load scenario that would surface throughput limits, Service Bus depth behaviour under stress, or database connection-pool exhaustion.

The right tool for this in the .NET ecosystem is NBomber, which integrates cleanly with the existing test infrastructure. A reasonable first scenario: publish 10,000 messages to the `dataflow-transform` queue, observe end-to-end latency distribution, and observe DLQ rate. This is a one-day implementation against the existing wiring.

### No chaos tests beyond the Complex API's built-in 5%

`ComplexApiClient` has built-in chaos: 5% of requests return 503 with a `Retry-After` header. This is verified in the HTTP client tests. There is no broader chaos framework that injects faults into the SB layer, the storage layer, or the database layer.

The right approach here is to use Azure Chaos Studio against a non-production environment after the VNET topology is in place. The dead-letter classifier's behaviour under different fault types is the most interesting target: does the heuristic rule still classify a stuck connection-pool exhaustion correctly? Does the timer-driven replay back off appropriately when SB itself is degraded? These questions are answerable with chaos experiments rather than synthetic argument.

### No contract tests against external APIs

In the current state, the only external API FileIt calls is the Complex API simulation, which is a project in the same repo and is therefore implicitly contract-tested by the unit tests on `ComplexApiClient`.

In a Provider-migration future where FileIt calls PrivateLink, Salesforce, LP Vision, or Bloomberg, contract testing matters. The right tool is Pact, which records consumer expectations as test artifacts and lets the provider verify against them in a separate workflow. This is a pattern that maps cleanly onto the existing `IComplexApiClient` interface seam.

### No synthetic transactions in cloud

A synthetic transaction is a scheduled producer that emits a known test message at a known interval and validates that the downstream consumer processed it. The output is a continuous binary signal: pipeline is alive or pipeline is dead.

FileIt does not have this. The lab verification done on 2026-05-13 was a one-shot. A synthetic transaction would emit a `synthetic-{timestamp}.txt` into `dataflow-source` every five minutes and a separate timer-triggered Function would assert that the corresponding `summary_synthetic-{timestamp}.csv` appeared in `dataflow-final` within a defined SLO. The output of the assertion would be a heartbeat metric in App Insights, which would feed a production alert.

This is a high-value addition for production but does not block lab verification.

### No automated KQL assertions

The four KQL queries committed to `docs/queries/appinsights/` are tools for an operator to run by hand. They are not assertions. An operator who runs the dataflow happy-path query and sees three results instead of the expected eight has to notice that gap manually.

The right next step is to convert the most important queries into Azure Monitor scheduled query alerts. Two examples: alert when the DLQ ingestion rate exceeds N records per hour, alert when the end-to-end latency for the dataflow happy-path exceeds M seconds. These alerts are the cloud-side equivalent of an integration test, and they belong in the same operational toolkit as the queries themselves.

### No OpenAPI contract for Complex.Host

`Complex.Host` exposes HTTP endpoints with a Swagger UI but the OpenAPI spec is generated implicitly rather than committed to source. A committed spec at `docs/openapi/complex.yaml` would enable automated contract verification between the consumer (`services.ApiAddCommand` via `IComplexApiClient`) and the provider (`Complex.Host`). The verification would catch a breaking change to the Complex API at PR time rather than at deploy time.

This is a small piece of work and the right immediate next step in the testability backlog.

### No mutation testing

Mutation testing modifies the production code in small ways (a `>` becomes a `>=`, a `+` becomes a `-`) and re-runs the test suite. Any mutation that passes the test suite indicates a behaviour the tests are not covering. The Stryker.NET tool is the right choice for the .NET ecosystem.

For FileIt's current size, mutation testing would be revealing on the dead-letter classifier (where the precise priority order of rules matters) and on the App layer commands (where the conditional logic is dense). This is a quarter-scale piece of work rather than a week-scale piece of work and belongs further out in the backlog.

## Recommendations in priority order

1. Commit a published OpenAPI spec for `Complex.Host` and add a contract verification check to CI. One day of work. Highest leverage per unit of effort.
2. Add an NBomber-driven load test scenario for the dataflow happy path. One day of work. Produces the first data point on real throughput.
3. Convert the most important KQL queries into Azure Monitor scheduled query alerts. Two days of work after the VNET topology lands. Produces the first production observability signal.
4. Add a synthetic transaction in cloud for the dataflow happy path. Two days of work. Produces the first continuous health signal.
5. Run Stryker.NET against `FileIt.Infrastructure` and triage the surviving mutants. One week of work, mostly triage. Produces a sharper picture of where the existing test coverage is weakest.
6. Introduce Pact contract tests once the first Provider migration touches a real external API. Out of scope until then.
7. Run Azure Chaos Studio experiments against a non-production VNET-deployed environment once the VNET topology is in place. Out of scope until then.

## What is already good enough

For a project of FileIt's current scope, the testability posture is honest and complete. The unit tests cover the layers that hold business decisions. The architecture tests prevent the kind of structural drift that erodes testability over time. The integration tests cover the wiring that the unit tests cannot. The end-to-end cloud verification proves that the lab posture transfers to real Azure infrastructure. The remaining gaps are forward-looking rather than corrective.

The most important property the test suite gives the project is the ability to refactor aggressively. Every change to a production class is followed by `dotnet test` in three seconds, and the result is binary. That is what enables the kind of bug-discovery loop demonstrated by issues #6, #16, and tonight's ComplexApi connection-string fix: notice, change, verify, commit. Without the test discipline, each of those changes would have been a half-day of manual verification. With it, they are minutes.

The story document captures this same point in different language. This review is the audit-style answer to the question Cesar asked in issue #27: yes, the system is open to being tested, and the testing is automated to the degree that the project's current scope justifies. The next degree of automation is named, scoped, and prioritized in the recommendations above.
