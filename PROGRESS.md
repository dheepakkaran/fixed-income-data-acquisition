# PROGRESS

## Done
- **Setup** — solution skeleton (10 projects), build config, `docker-compose.yml`
  (Postgres 16 on host port 55432), minimal `ci.yml`. Repo public, `main` and `develop`
  protected, Pages enabled (source: GitHub Actions).
- **Live research** — SEC ABS-EE and Treasury endpoints, formats and field names verified
  against real downloads and the official XSD (ABS XML Technical Specification v3.1).
- **`feature/core-pipeline`** (PR #1, merged) — `IDataProvider`, `DataProviderBase` template
  method, `IngestionRun`, `ProviderRegistry`, `ValidationResult`.
- **`feature/storage-postgres`** (PR #2, merged) — DbUp migrations, `CopyBulkLoader`
  (binary COPY into staging then one upsert, one transaction), `InsertBulkLoader` baseline,
  Dapper `IngestionRunStore`, filesystem raw-file cache.
- **`feature/provider-sec-abs-ee`** — streaming `XmlReader` parser, EDGAR client, paced rate
  limiter, validator, migration `0002_auto_loan_asset_data.sql`, CLI (`migrate`, `ingest`,
  `status`). **316,091 real loan-level records ingested** from 3 trusts x 3 periods.
  83 tests, 70.7% line coverage. See RESULTS.md.

## Next
- `feature/provider-treasury-yields`

## Open issues
- `perf-gate` is not yet a required status check on `main`; it is added in
  `feature/perf-gate` (`docs/decisions.md` D1).
- `docs/metrics.md` must state that ABS-EE rates are decimal fractions while Treasury par
  yields are percentages. Getting that conversion wrong is a 100x error in the spread.
- The report, its SQL views and GitHub Pages deployment are not built yet.
