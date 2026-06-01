# ============================================================================
# generate-test-results.ps1
# Runs the whole test suite, collects every results.trx, and writes a single
# test-results.json into the UI web folder so the operator console can render
# a live-looking pass/fail dashboard. Re-run this whenever you want to refresh
# the snapshot the console shows.
# ============================================================================

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
[Environment]::CurrentDirectory = $repo
Set-Location $repo

Write-Host "Running full test suite with trx logging..." -ForegroundColor Cyan
dotnet test "$repo\FileIt.All.sln" -c Release --logger "trx;LogFileName=results.trx" | Out-Host

Write-Host "Collecting results.trx files..." -ForegroundColor Cyan
$trxFiles = Get-ChildItem -Path $repo -Recurse -Filter "results.trx" -File

$ns = @{ t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010" }
$projects = @{}
$grandTotal = 0; $grandPassed = 0; $grandFailed = 0; $grandSkipped = 0

foreach ($trx in $trxFiles) {
    # Project name = the test project folder (…\<Project>\TestResults\results.trx → <Project>)
    $projectName = Split-Path (Split-Path $trx.FullName -Parent) -Parent | Split-Path -Leaf

    [xml]$doc = Get-Content $trx.FullName -Raw

    # Map testId/executionId -> className from TestDefinitions.
    $classByName = @{}
    foreach ($ut in $doc.TestRun.TestDefinitions.UnitTest) {
        if ($ut.TestMethod) {
            $classByName[$ut.name] = $ut.TestMethod.className
        }
    }

    $tests = @()
    foreach ($r in $doc.TestRun.Results.UnitTestResult) {
        $tests += [pscustomobject]@{
            name      = $r.testName
            className = $classByName[$r.testName]
            outcome   = $r.outcome
            duration  = $r.duration
        }
        $grandTotal++
        switch ($r.outcome) {
            "Passed"        { $grandPassed++ }
            "Failed"        { $grandFailed++ }
            default         { $grandSkipped++ }
        }
    }

    if (-not $projects.ContainsKey($projectName)) {
        $projects[$projectName] = @()
    }
    $projects[$projectName] += $tests
}

# Shape into an array of project groups.
$projectGroups = foreach ($name in ($projects.Keys | Sort-Object)) {
    $list = $projects[$name]
    [pscustomobject]@{
        project = $name
        total   = @($list).Count
        passed  = @($list | Where-Object { $_.outcome -eq "Passed" }).Count
        failed  = @($list | Where-Object { $_.outcome -eq "Failed" }).Count
        tests   = $list
    }
}

$payload = [pscustomobject]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    total        = $grandTotal
    passed       = $grandPassed
    failed       = $grandFailed
    skipped      = $grandSkipped
    projects     = @($projectGroups)
}

$outDir = "$repo\FileIt.Module.Ui\FileIt.Module.Ui.Host\web\public"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$outFile = "$outDir\test-results.json"
$payload | ConvertTo-Json -Depth 6 | Out-File -FilePath $outFile -Encoding utf8

Write-Host "Wrote $outFile" -ForegroundColor Green
Write-Host "Total $grandTotal  Passed $grandPassed  Failed $grandFailed  Skipped $grandSkipped" -ForegroundColor Green