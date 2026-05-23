# FileIt.Module.Ui

Scaffolded by `dotnet new fileit-module`.

## Structure

| Project | Role |
|---|---|
| `FileIt.Module.Ui.Host` | Azure Functions host. Contains the function entry points (Health, Watcher, Subscriber, DeadLetterReader, Test). |
| `FileIt.Module.Ui.App` | Application logic. Contains the use-case classes (WatchInbound, BasicApiAddHandler), config, events, message DTOs. |
| `FileIt.Module.Ui.Test` | Unit tests for the App project (MSTest). |
| `FileIt.Module.Ui.Integration` | Integration tests (MSTest, run against the emulator stack). |

## First three files to edit

1. **`FileIt.Module.Ui.App/UiEvents.cs`** — confirm your EventId block doesn't collide with another module's. Existing blocks: SimpleFlow=2000, DataFlow=3000, Services=1000.
2. **`FileIt.Module.Ui.App/WatchInbound/WatchInbound.cs`** — replace the boilerplate "move blob, log it, queue an API call" with whatever your module actually does on inbound.
3. **`FileIt.Module.Ui.Host/appsettings.json`** — adjust `QueueName`, container names, and EventId values to match your business semantics.

## Local dev

```bash
dotnet build
cd FileIt.Module.Ui.Host
func start
```

The local HTTP port is set in `Properties/launchSettings.json`. Adjust if it collides with another running module.

## Conventions enforced by this template

- `dotnet-isolated` worker, .NET 10, Functions runtime v4
- Service Bus topic + subscription pattern: `api-add-topic` / `api-add-ui-sub`
- Dead-letter pipeline auto-wired via `IDeadLetterIngestionService` (see `UiDeadLetterReader.cs`)
- Logging via `MiddlewareLogger` + `SerilogInvocationIdMiddleware` + `ExceptionHandlingMiddleware`
- `ICommonLogConfig` populated from configuration, applied to all sinks (App Insights, file, console)

## Wiring into the solution

The wrapper script `scripts/new-fileit-module.ps1` adds these projects to `FileIt.All.sln` automatically. If you scaffold from `dotnet new` directly, run:

```powershell
dotnet sln FileIt.All.sln add FileIt.Module.Ui/FileIt.Module.Ui.Host/FileIt.Module.Ui.Host.csproj
dotnet sln FileIt.All.sln add FileIt.Module.Ui/FileIt.Module.Ui.App/FileIt.Module.Ui.App.csproj
dotnet sln FileIt.All.sln add FileIt.Module.Ui/FileIt.Module.Ui.Test/FileIt.Module.Ui.Test.csproj
dotnet sln FileIt.All.sln add FileIt.Module.Ui/FileIt.Module.Ui.Integration/FileIt.Module.Ui.Integration.csproj
```
