Add-Content -Path C:\Windows\System32\drivers\etc\hosts -Value "`n13.71.170.130  fa-fileit35-ui-04199dab5cabcd.scm.azurewebsites.net"<#
.SYNOPSIS
    Scaffolds a new FileIt module from the dotnet template, wires it into the
    solution, and verifies it builds. Solo command for the entire workflow.

.DESCRIPTION
    Wraps `dotnet new fileit-module` with the cross-cutting concerns the bare
    template can't handle on its own: name validation, solution registration,
    smoke build, dry-run preview, and reversible teardown.

.PARAMETER Name
    PascalCase module name. Must match ^[A-Z][A-Za-z0-9]+$. Reserved names
    (DataFlow, Services, SimpleFlow, Common, Domain, Infrastructure, Database)
    are rejected.

.PARAMETER QueuePrefix
    Lowercase, hyphen-separated prefix for Service Bus queues, topics, blob
    containers. Defaults to lowercased Name. Must match ^[a-z][a-z0-9-]+$.

.PARAMETER EventIdBase
    Starting EventId for the module's logs. Existing blocks: SimpleFlow=2000,
    DataFlow=3000, Services=1000. Must be a multiple of 1000 between 4000-99000.

.PARAMETER LocalHttpPort
    Local dev HTTP port. Existing modules: SimpleFlow=7060, DataFlow=7061,
    Services=7062. Must be in 7050-7099 and not in use by another module's
    launchSettings.json.

.PARAMETER NoWatcher
    Omit the EventGrid blob watcher.

.PARAMETER NoSubscriber
    Omit the Service Bus subscriber. Implies -NoDeadLetterReader.

.PARAMETER NoDeadLetterReader
    Omit the dead-letter reader function.

.PARAMETER NoTestSeeder
    Omit the timer-triggered blob seeder.

.PARAMETER DryRun
    Resolve all parameters, validate, print what would happen. Touches nothing.

.PARAMETER Remove
    Tear down a previously scaffolded module: remove from sln, delete folder.
    Refuses to touch the three protected modules (DataFlow, Services, SimpleFlow).

.PARAMETER SolutionPath
    Path to the .sln file to register projects into. Defaults to
    `FileIt.All.sln` in the repo root.

.EXAMPLE
    .\scripts\new-fileit-module.ps1 -Name TradeRecon

    Scaffolds FileIt.Module.TradeRecon with all defaults derived from the name.

.EXAMPLE
    .\scripts\new-fileit-module.ps1 -Name TradeRecon -EventIdBase 4000 -LocalHttpPort 7063 -DryRun

    Previews the scaffold without writing any files.

.EXAMPLE
    .\scripts\new-fileit-module.ps1 -Name TradeRecon -Remove

    Removes a previously scaffolded TradeRecon module.

.NOTES
    Prereq: the template is installed once with
        dotnet new install ./templates/FileIt.Module
    Run from the repo root.
#>

[CmdletBinding(DefaultParameterSetName = "Create")]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name,

    [Parameter(ParameterSetName = "Create")]
    [string]$QueuePrefix,

    [Parameter(ParameterSetName = "Create")]
    [int]$EventIdBase = 0,

    [Parameter(ParameterSetName = "Create")]
    [int]$LocalHttpPort = 0,

    [Parameter(ParameterSetName = "Create")]
    [switch]$NoWatcher,

    [Parameter(ParameterSetName = "Create")]
    [switch]$NoSubscriber,

    [Parameter(ParameterSetName = "Create")]
    [switch]$NoDeadLetterReader,

    [Parameter(ParameterSetName = "Create")]
    [switch]$NoTestSeeder,

    [Parameter(ParameterSetName = "Create")]
    [switch]$DryRun,

    [Parameter(ParameterSetName = "Remove")]
    [switch]$Remove,

    [string]$SolutionPath = ".\FileIt.All.sln"
)

$ErrorActionPreference = "Stop"

# ---- Constants -----------------------------------------------------------

$ReservedModuleNames = @(
    "DataFlow", "Services", "SimpleFlow",
    "Common", "Domain", "Infrastructure", "Database",
    "Module", "FileIt", "FileItModule",
    "Test", "Integration", "Host", "App"
)

$ProtectedModulesForRemoval = @("DataFlow", "Services", "SimpleFlow")

# Existing module reservations - update if you add a new module without this script.
$ReservedEventIdBlocks = @{
    1000 = "Services"
    2000 = "SimpleFlow"
    3000 = "DataFlow"
}
$ReservedHttpPorts = @{
    7060 = "SimpleFlow"
    7061 = "DataFlow"
    7062 = "Services"
}

# ---- Helpers -------------------------------------------------------------

function Write-Section([string]$Title) {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor DarkGray
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor DarkGray
}

function Write-Step([string]$Msg) {
    Write-Host "  [step] $Msg" -ForegroundColor White
}

function Write-Ok([string]$Msg) {
    Write-Host "  [ok]   $Msg" -ForegroundColor Green
}

function Write-Warn([string]$Msg) {
    Write-Host "  [warn] $Msg" -ForegroundColor Yellow
}

function Write-Err([string]$Msg) {
    Write-Host "  [err]  $Msg" -ForegroundColor Red
}

function Test-PascalCase([string]$Value) {
    return $Value -cmatch '^[A-Z][A-Za-z0-9]+$'
}

function Test-QueuePrefix([string]$Value) {
    return $Value -cmatch '^[a-z][a-z0-9-]+$'
}

function Resolve-RepoRoot {
    # Walk up from script location to find the .sln
    $here = Split-Path -Parent $PSCommandPath
    $candidate = Split-Path -Parent $here
    if (Test-Path (Join-Path $candidate "FileIt.All.sln")) {
        return $candidate
    }
    if (Test-Path (Join-Path (Get-Location) "FileIt.All.sln")) {
        return (Get-Location).Path
    }
    throw "Could not locate FileIt.All.sln. Run from repo root or place script in scripts\ subfolder."
}

function Test-TemplateInstalled {
    $output = dotnet new list fileit-module 2>&1 | Out-String
    return $output -match "fileit-module"
}

# ---- Remove path ---------------------------------------------------------

if ($Remove) {
    if ($ProtectedModulesForRemoval -contains $Name) {
        Write-Err "Refusing to remove protected module '$Name'. This script will not touch DataFlow, Services, or SimpleFlow."
        exit 1
    }

    if (-not (Test-PascalCase $Name)) {
        Write-Err "Module name must be PascalCase: $Name"
        exit 1
    }

    $repoRoot = Resolve-RepoRoot
    $moduleDir = Join-Path $repoRoot "FileIt.Module.$Name"
    $slnFull = Join-Path $repoRoot $SolutionPath.TrimStart(".\")

    Write-Section "Removing module: $Name"

    if (-not (Test-Path $moduleDir)) {
        Write-Warn "Module folder does not exist: $moduleDir"
    }

    # Remove projects from solution
    $projects = @(
        "FileIt.Module.$Name\FileIt.Module.$Name.Host\FileIt.Module.$Name.Host.csproj",
        "FileIt.Module.$Name\FileIt.Module.$Name.App\FileIt.Module.$Name.App.csproj",
        "FileIt.Module.$Name\FileIt.Module.$Name.Test\FileIt.Module.$Name.Test.csproj",
        "FileIt.Module.$Name\FileIt.Module.$Name.Integration\FileIt.Module.$Name.Integration.csproj"
    )

    Push-Location $repoRoot
    try {
        if (Test-Path $slnFull) {
            foreach ($p in $projects) {
                if (Test-Path (Join-Path $repoRoot $p)) {
                    Write-Step "sln remove: $p"
                    & dotnet sln $slnFull remove $p 2>&1 | Out-Null
                }
            }
        }

        if (Test-Path $moduleDir) {
            Write-Step "Deleting folder: $moduleDir"
            Remove-Item -Recurse -Force $moduleDir
        }

        Write-Ok "Module '$Name' removed."
    }
    finally {
        Pop-Location
    }
    exit 0
}

# ---- Create path ---------------------------------------------------------

Write-Section "Scaffold FileIt module: $Name"

# Validate name
if (-not (Test-PascalCase $Name)) {
    Write-Err "Module name must be PascalCase (^[A-Z][A-Za-z0-9]+`$). Got: '$Name'"
    exit 1
}
if ($ReservedModuleNames -contains $Name) {
    Write-Err "Module name '$Name' is reserved. Pick something else."
    exit 1
}

# Derive defaults
if (-not $QueuePrefix) {
    $QueuePrefix = $Name.ToLowerInvariant()
}
if (-not (Test-QueuePrefix $QueuePrefix)) {
    Write-Err "QueuePrefix must be lowercase alphanumeric with optional hyphens. Got: '$QueuePrefix'"
    exit 1
}

if ($EventIdBase -eq 0) {
    # Find first free 1000-block starting at 4000
    $candidate = 4000
    while ($ReservedEventIdBlocks.ContainsKey($candidate)) {
        $candidate += 1000
    }
    $EventIdBase = $candidate
    Write-Step "EventIdBase auto-selected: $EventIdBase"
}
if ($EventIdBase -lt 4000 -or $EventIdBase -gt 99000 -or ($EventIdBase % 1000) -ne 0) {
    Write-Err "EventIdBase must be a multiple of 1000 between 4000 and 99000. Got: $EventIdBase"
    exit 1
}
if ($ReservedEventIdBlocks.ContainsKey($EventIdBase)) {
    Write-Err "EventIdBase $EventIdBase is reserved by module '$($ReservedEventIdBlocks[$EventIdBase])'. Pick a different block."
    exit 1
}

if ($LocalHttpPort -eq 0) {
    $candidate = 7063
    while ($ReservedHttpPorts.ContainsKey($candidate) -and $candidate -le 7099) {
        $candidate++
    }
    $LocalHttpPort = $candidate
    Write-Step "LocalHttpPort auto-selected: $LocalHttpPort"
}
if ($LocalHttpPort -lt 7050 -or $LocalHttpPort -gt 7099) {
    Write-Err "LocalHttpPort must be between 7050 and 7099. Got: $LocalHttpPort"
    exit 1
}
if ($ReservedHttpPorts.ContainsKey($LocalHttpPort)) {
    Write-Err "LocalHttpPort $LocalHttpPort is reserved by module '$($ReservedHttpPorts[$LocalHttpPort])'. Pick a different port."
    exit 1
}

# Implies
if ($NoSubscriber) {
    $NoDeadLetterReader = $true
}

# Resolve paths
$repoRoot = Resolve-RepoRoot
$moduleDir = Join-Path $repoRoot "FileIt.Module.$Name"
$slnFull = Join-Path $repoRoot $SolutionPath.TrimStart(".\")

# Pre-flight
if (Test-Path $moduleDir) {
    Write-Err "Module folder already exists: $moduleDir"
    Write-Err "Use -Remove first if you want to recreate it."
    exit 1
}
if (-not (Test-TemplateInstalled)) {
    Write-Err "Template 'fileit-module' is not installed."
    Write-Err "Run once: dotnet new install ./templates/FileIt.Module"
    exit 1
}

# Print plan
Write-Host ""
Write-Host "Plan:" -ForegroundColor Cyan
Write-Host "  Name              : $Name"
Write-Host "  QueuePrefix       : $QueuePrefix"
Write-Host "  EventIdBase       : $EventIdBase"
Write-Host "  LocalHttpPort     : $LocalHttpPort"
Write-Host "  Watcher           : $(-not $NoWatcher)"
Write-Host "  Subscriber        : $(-not $NoSubscriber)"
Write-Host "  DeadLetterReader  : $(-not $NoDeadLetterReader)"
Write-Host "  TestSeeder        : $(-not $NoTestSeeder)"
Write-Host "  Output            : $moduleDir"
Write-Host "  Solution          : $slnFull"
Write-Host ""

if ($DryRun) {
    Write-Ok "Dry run complete. Nothing was written."
    exit 0
}

# Run dotnet new
Write-Section "dotnet new fileit-module"

$dotnetNewArgs = @(
    "new", "fileit-module",
    "--name", $Name,
    "--output", $moduleDir,
    "--QueuePrefix", $QueuePrefix,
    "--EventIdBase", "$EventIdBase",
    "--LocalHttpPort", "$LocalHttpPort"
)

if ($NoWatcher)            { $dotnetNewArgs += @("--IncludeWatcher", "false") }
if ($NoSubscriber)         { $dotnetNewArgs += @("--IncludeSubscriber", "false") }
if ($NoDeadLetterReader)   { $dotnetNewArgs += @("--IncludeDeadLetterReader", "false") }
if ($NoTestSeeder)         { $dotnetNewArgs += @("--IncludeTestSeeder", "false") }

Write-Step "dotnet $($dotnetNewArgs -join ' ')"
& dotnet @dotnetNewArgs
if ($LASTEXITCODE -ne 0) {
    Write-Err "dotnet new failed with exit code $LASTEXITCODE"
    if (Test-Path $moduleDir) { Remove-Item -Recurse -Force $moduleDir }
    exit $LASTEXITCODE
}
Write-Ok "Files generated."

# Register projects in solution
Write-Section "Registering projects in solution"

$projects = @(
    "FileIt.Module.$Name\FileIt.Module.$Name.Host\FileIt.Module.$Name.Host.csproj",
    "FileIt.Module.$Name\FileIt.Module.$Name.App\FileIt.Module.$Name.App.csproj",
    "FileIt.Module.$Name\FileIt.Module.$Name.Test\FileIt.Module.$Name.Test.csproj",
    "FileIt.Module.$Name\FileIt.Module.$Name.Integration\FileIt.Module.$Name.Integration.csproj"
)

Push-Location $repoRoot
try {
    if (-not (Test-Path $slnFull)) {
        Write-Warn "Solution file not found: $slnFull. Skipping sln registration."
    } else {
        $added = 0
        $missing = @()
        foreach ($p in $projects) {
            $abs = Join-Path $repoRoot $p
            if (Test-Path $abs) {
                Write-Step "sln add: $p"
                & dotnet sln $slnFull add $p
                if ($LASTEXITCODE -ne 0) {
                    Write-Warn "sln add reported non-zero exit for $p (likely already added)"
                } else {
                    $added++
                }
            } else {
                $missing += $p
            }
        }
        if ($added -eq 0) {
            Write-Err "No projects were added to the solution. Expected paths missing:"
            foreach ($m in $missing) { Write-Err "  $m" }
            Write-Err "Template likely produced files at the wrong location. Check $moduleDir."
            exit 1
        }
        Write-Ok "Projects registered ($added added)."
    }

    # Smoke build - build the master sln since the new module's projects
    # are now part of it. dotnet build <directory> only works when the dir
    # contains exactly one project/sln, which is not true for a module folder.
    Write-Section "Smoke build"
    Write-Step "dotnet build $slnFull"
    & dotnet build $slnFull --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Build failed. Module was generated but does not compile cleanly."
        Write-Err "Investigate, then either fix or run with -Remove $Name to delete."
        exit $LASTEXITCODE
    }
    Write-Ok "Build succeeded."
}
finally {
    Pop-Location
}

# Done
Write-Section "Done"
Write-Host ""
Write-Host "Module 'FileIt.Module.$Name' is ready." -ForegroundColor Green
Write-Host ""
Write-Host "First three files to edit:" -ForegroundColor Cyan
Write-Host "  1. $moduleDir\FileIt.Module.$Name.App\$($Name)Events.cs"
Write-Host "  2. $moduleDir\FileIt.Module.$Name.App\WatchInbound\WatchInbound.cs"
Write-Host "  3. $moduleDir\FileIt.Module.$Name.Host\appsettings.json"
Write-Host ""
Write-Host "To run locally:" -ForegroundColor Cyan
Write-Host "  cd $moduleDir\FileIt.Module.$Name.Host"
Write-Host "  func start"
Write-Host ""
Write-Host "To remove cleanly:" -ForegroundColor Cyan
Write-Host "  .\scripts\new-fileit-module.ps1 -Name $Name -Remove"
Write-Host ""


Add-Content -Path C:\Windows\System32\drivers\etc\hosts -Value "`n13.71.170.130  fa-fileit35-ui-04199dab5cabcd.azurewebsites.net"