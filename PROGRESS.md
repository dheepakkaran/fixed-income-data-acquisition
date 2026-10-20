# PROGRESS

## Done
- **Setup** — solution skeleton (10 projects), `.gitignore`, `.editorconfig`,
  `Directory.Build.props` (net10.0, nullable, warnings-as-errors), `docker-compose.yml`
  (Postgres 16 on host port 55432), minimal `ci.yml` with job `build-test`.
  Repo public, `main` and `develop` protected, Pages enabled (source: GitHub Actions).
- **Live research** — SEC ABS-EE and Treasury endpoints, formats and field names verified
  against real downloads and the official XSD (ABS XML Technical Specification v3.1,
  2023-09-18). Trusts chosen: GM Financial Consumer Auto 2026-1 (CIK 2099048, prime),
  AmeriCredit Auto 2024-1 (CIK 2020251, subprime), Exeter Auto 2024-2 (CIK 2013062,
  deep subprime).
- **`feature/core-pipeline`** (PR #1, merged) — `IDataProvider`, `DataProviderBase`
  template method, `IngestionRun`, `ProviderRegistry`, `ValidationResult`. 18 unit tests.
- **`feature/storage-postgres`** — DbUp migration `0001_ingestion_tracking.sql`,
  `CopyBulkLoader` (binary COPY into a temp staging table, then one upsert, one
  transaction), `InsertBulkLoader` (the measured baseline), Dapper `IngestionRunStore`,
  filesystem raw-file cache. 15 integration tests on a real Postgres 16 via Testcontainers.

## Next
- `feature/provider-sec-abs-ee`

## Open issues
- `perf-gate` is not yet a required status check on `main`; it is added in
  `feature/perf-gate` (see `docs/decisions.md` D1).
- RESULTS.md is not written yet: it waits for measurements against real files.
