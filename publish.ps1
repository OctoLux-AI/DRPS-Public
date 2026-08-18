<#
.SYNOPSIS
    Publishes Drps.Ingestion, Drps.Calculator, and Drps.Gate to fixed output folders.

.DESCRIPTION
    Windows Task Scheduler currently launches all three workers via bare `dotnet run`,
    which triggers an implicit build/restore on every process launch. When all three
    tasks fire at the same StartBoundary, they collide on the same solution tree -
    confirmed root cause of Ingestion and Calculator both exiting with code 1 on
    2026-07-28's unattended run, before either wrote a log line.

    This script publishes each worker once, ahead of time, to a fixed folder. Task
    Scheduler can then launch the published .exe directly (a separate, later change -
    not made by this script) instead of `dotnet run`, removing the implicit-build
    collision entirely.

    Framework-dependent (no --self-contained, no RuntimeIdentifier) - matches every
    project's existing csproj, which sets neither. No application logic, config
    loading, or worker behavior is touched by this script.

    Also copies shared-watchlist.appsettings.json from the repo root to publish\
    (one level above the three per-project output folders) - Drps.Ingestion's and
    Drps.Calculator's Program.cs both load this file via
    Path.Combine(ContentRootPath, "..", "shared-watchlist.appsettings.json") with
    optional: false. That relative path is correct for `dotnet run` (ContentRootPath
    = the project directory, ".." = repo root, where the file already lives), but a
    published exe's ContentRootPath is its own output folder (e.g. publish\Ingestion),
    whose ".." is publish\ - not the repo root - so the file was silently absent
    there. Confirmed root cause (2026-07-28 audit) of Ingestion/Calculator crashing
    in builder.Build() before any logger initializes: exit code 1, zero log output.
    Drps.Gate has no such dependency and was unaffected - this copy step doesn't
    touch Gate's output folder.

.EXAMPLE
    .\publish.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot

$targets = @(
    @{ Project = "Drps.Ingestion";  Output = Join-Path $repoRoot "publish\Ingestion" },
    @{ Project = "Drps.Calculator"; Output = Join-Path $repoRoot "publish\Calculator" },
    @{ Project = "Drps.Gate";       Output = Join-Path $repoRoot "publish\Gate" }
)

foreach ($target in $targets) {
    $projectPath = Join-Path $repoRoot "$($target.Project)\$($target.Project).csproj"
    Write-Host "==> Publishing $($target.Project) to $($target.Output)"
    dotnet publish $projectPath -c Release -o $target.Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($target.Project) with exit code $LASTEXITCODE"
    }
}

# Drps.Ingestion and Drps.Calculator both require this file one level above their own
# ContentRootPath (optional: false - a missing copy is a startup-time crash, not a
# silent skip). Copied once to publish\ (the shared parent of all three per-project
# output folders), not per-project - matches the single file Program.cs's relative
# path already resolves to. Drps.Gate has no dependency on this file; nothing is
# copied into publish\Gate.
$sourceWatchlist = Join-Path $repoRoot "shared-watchlist.appsettings.json"
$destWatchlist = Join-Path $repoRoot "publish\shared-watchlist.appsettings.json"
Write-Host "==> Copying shared-watchlist.appsettings.json to $destWatchlist"
Copy-Item -Path $sourceWatchlist -Destination $destWatchlist -Force

Write-Host "`nAll three projects published successfully:"
foreach ($target in $targets) {
    $exePath = Join-Path $target.Output "$($target.Project).exe"
    Write-Host "  $($target.Project): $exePath"
}
