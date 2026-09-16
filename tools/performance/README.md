# Local M0.3 performance tooling

This directory provides the local-only performance baseline inputs and runner.
It does not create a test project, run browser automation, access network content,
or write a database. All generated fixture, trace, database, history, package
capture, and memory data must stay in ignored `.artifacts/performance/` paths.

Generate the two supported v30 project-import fixtures and validate their
manifest from the repository root:

```powershell
$fixturePath = Join-Path (Get-Location) '.artifacts\performance\m0.3-fixtures-a'
dotnet run --project tools\performance\Lorekeeper.PerformanceFixtures.csproj -- --output $fixturePath
dotnet run --project tools\performance\Lorekeeper.PerformanceFixtures.csproj -- --output $fixturePath --verify
```

The manifest fixes seed `Lorekeeper-M0.3-v1`, v30, counts, text byte lengths,
and SHA-256 values. `--include-large-sources` deliberately generates 50
deterministic 64 MiB ASCII source files (3.125 GiB) only when explicitly asked.
Those files are not an M0.3 measured workload: the current import surface has a
64 MiB full-memory limit and M4 owns streaming source-library work.

Build the unsigned local Windows Release package with
`scripts/build-windows-release.ps1 -KeepUnpacked`, after confirming that its
ignored `publish/win-x64-stage` and `publish/win-x64` directories are disposable.
Then run the local sampler:

```powershell
$packagePath = Join-Path (Get-Location) 'publish\win-x64\win-unpacked'
$resultsPath = Join-Path (Get-Location) '.artifacts\performance\m0.3-session'
.\tools\performance\Measure-M0Performance.ps1 -PackageDirectory $packagePath -FixtureDirectory $fixturePath -ResultsDirectory $resultsPath
```

The runner validates the fixture manifest, starts the Release Electron binary
with isolated database, history, and Electron user-data paths, turns off updater
behavior through the Development environment, samples the package process tree
every 250 ms, and writes raw timing and package/host evidence. During that authorized local app
session, load the generated JSON only via Lorekeeper's production import UI—do
not populate SQLite directly. Complete five warm-ups, then exactly 30 measured
samples each of input-to-visible-frame, save acknowledgment, Undo, Redo, and
alternating chapter navigation; do typing and autosave before Undo/Redo so their
forced save does not enter the autosave sample set. The runner limits each metric
to those 35 events, uses nearest-rank p95, and fails closed if any metric does
not contain exactly five warm-ups plus 30 samples.
