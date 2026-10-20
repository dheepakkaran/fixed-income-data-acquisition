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

- **`feature/release-workflows`** — `release.yml` (publishes a self-contained CLI for
  linux-x64 and osx-arm64 on a `v*` tag) and `production-run.yml` (monthly schedule plus
  manual dispatch, main only, Postgres service, migrations, ingest, logs as an artifact,
  SEC downloads cached by month). The Pages deployment step is added with the report.

- **`fix/production-run-reliability`** (PR #6, merged) — `pipefail` on the ingest step (the
  first production run reported success with a failed source), application-level download
  retry with a `Content-Length` check, and a rate-limiter test that no longer measures the
  thread pool.
- **`v0.1.0` released** — tag, GitHub Release, self-contained CLI for linux-x64 and
  osx-arm64, production run executed from `main`.
- **`feature/provider-treasury-yields`** — second provider, second asset class. 6,255 par
  yield points over 2 calendar years. `git diff develop --stat -- src/DataAcquisition.Core`
  prints nothing.

- **`feature/abs-pool-report`** — SQL views (`v_pool_period`, `v_pool_cumulative_loss`,
  `v_pool_spread`), `docs/metrics.md`, static HTML report with inline SVG charts and no
  JavaScript, a CSV per trust, and `report --out ./site`. The production run now generates
  the report and deploys it to Pages in a separate job, so nothing publishes when ingestion
  failed.

## Next
- `feature/perf-gate`

## Open issues
- `perf-gate` is not yet a required status check on `main`; it is added in
  `feature/perf-gate` (`docs/decisions.md` D1).
- `docs/metrics.md` must state that ABS-EE rates are decimal fractions while Treasury par
  yields are percentages. Getting that conversion wrong is a 100x error in the spread.
- The benchmarks and the performance-regression gate are not built yet.
