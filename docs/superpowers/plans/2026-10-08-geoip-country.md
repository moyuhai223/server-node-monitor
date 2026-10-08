# GeoIP Country Correction Implementation Plan

> **For agentic workers:** Execute the approved plan task-by-task in the current
> session. Steps use checkbox syntax for tracking.

**Goal:** Correct stale/wrong automatic countries without interrupting monitoring.

**Architecture:** GeoIpService owns the immutable lookup snapshot and versioned
disk cache. NodeCountryService recalculates persisted automatic values; the
background worker and admin refresh endpoint both call it.

**Tech Stack:** .NET 10, EF Core SQLite, xUnit, Vue 3/Vite. No new dependencies.

**Spec:** ../specs/2026-10-08-geoip-country-design.md

## Global Constraints

- No Agent protocol change or additional package dependency.
- Keep the 7-day update interval and 6-hour background check (first after 10 s).
- Never hard-code the example IP into production logic.
- Preserve manual overrides and unrelated node edits.

## Task 1: Dataset and cache migration

Files: `GeoIpService.cs`, `SnmOptions.cs`, `EnvAlias.cs`, `appsettings.json`,
`tests/SNM.Master.Tests/GeoIpTests.cs`.

- [ ] Add failing tests for the new default and old-cache refresh requirement:
  `Assert.Contains("releases/download/latest", new SnmOptions().GeoIp.BaseUrl);`
  and `Assert.True(geo.NeedsRefresh());` after loading a recent legacy cache.
- [ ] Run `dotnet test tests/SNM.Master.Tests --filter FullyQualifiedName~GeoIp`.
- [ ] Add `Dataset = "server-country"` and effective source properties. Preserve
  `Lookup(IPAddress?)`, `TryLoadFromDiskAsync(CancellationToken)` and
  `RefreshAsync(CancellationToken)` interfaces. A snapshot contains both lookup
  tables, download time and source identity. `NeedsRefresh()` compares identity
  as well as age/readiness.
- [ ] Publish a generation with `File.Move(metaTmp, metaPath, overwrite: true)`
  only after both CSVs parse and meet minimum row counts; assign the immutable
  snapshot afterward. Delete failed staging generations and superseded generations
  best-effort. Validate cache generation IDs before deriving paths.
- [ ] Add offline lookup, full-refresh, restart, source-change, cancellation and
  malformed-data tests; rerun the focused command.

## Task 2: Recalculate nodes and integrate both refresh paths

Files: new `NodeCountryService.cs`, `Program.cs`, `MaintenanceServices.cs`,
`SettingsEndpoints.cs`, `tests/SNM.Master.Tests/GeoIpTests.cs`.

- [ ] Implement `Task<int> RecalculateAsync(CancellationToken ct)` with a gate.
  Query stored captured IPs, look up countries, and issue a conditional field-only
  update: `Where(n => n.Id == id && n.LastRemoteIp == ip &&
  n.CountryCodeAuto == previous).ExecuteUpdateAsync(s =>
  s.SetProperty(n => n.CountryCodeAuto, cc), ct)`.
- [ ] Update the matching registry country without replacing whole metadata;
  call `RaiseNodesChanged` for changed IDs. Leave unknown/private results alone.
- [ ] Register the singleton; call it after manual refresh and from the scheduled
  worker after a successful refresh/current cache load. Return on refresh failure.
- [ ] Add service/API assertions for US → SG, manual overrides, offline nodes,
  persisted results, real-time notification and idempotence.
- [ ] Run all `SNM.Master.Tests`.

## Task 3: Settings, documentation and final verification

Files: admin settings Vue view, `deploy/systemd/master.env.example`, `README.md`,
`docs/DATA.md`, `docs/DEPLOY.md`, `docs/IMPLEMENTATION_NOTES.md`, `NOTICE.md`.

- [ ] Expose dataset/base URL in the existing admin GeoIP status/export response
  and render them in the existing source label.
- [ ] Document new default URLs/filenames, `SNM_GEOIP_DATASET`, migration and manual
  refresh reconciliation. Update active docs; leave historical candidate designs.
- [ ] Run `dotnet test ServerNodeMonitor.slnx -c Release` and `npm run build` in
  `web/admin`; inspect `git diff --check` and final diff for scope and regressions.
- [ ] Record verification outcomes and leave the reviewed implementation locally
  on `codex/fix-geoip-country`; remote publication is a separate action.
