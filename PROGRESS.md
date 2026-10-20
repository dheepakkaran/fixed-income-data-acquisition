# PROGRESS

## Done

| Branch | PR | What |
|---|---|---|
| Setup (on `main`) | — | Solution skeleton, build config, `docker-compose.yml`, minimal `ci.yml`. Repo public, `main` and `develop` protected, Pages enabled. |
| `feature/core-pipeline` | #1 | `IDataProvider`, `DataProviderBase` template method, `IngestionRun`, `ProviderRegistry`, `ValidationResult`. |
| `feature/storage-postgres` | #2 | DbUp migrations, `CopyBulkLoader` (binary COPY → staging → one upsert, one transaction), `InsertBulkLoader` baseline, Dapper run store, raw-file cache. |
| `feature/provider-sec-abs-ee` | #3 | Streaming `XmlReader` parser, EDGAR client, paced rate limiter, validator, migration 0002, CLI (`migrate`, `ingest`, `status`). **316,091 real records.** |
| `feature/release-workflows` | #4 | `release.yml`, `production-run.yml`. |
| **`v0.1.0`** | #5 | Tagged, GitHub Release, self-contained CLI for linux-x64 and osx-arm64, production run executed from `main`. |
| `fix/production-run-reliability` | #6 | `pipefail` on the ingest step, download retry with a `Content-Length` check, a rate-limiter test that no longer measures the thread pool. |
| `feature/provider-treasury-yields` | #7 | Second provider, second asset class. 6,255 par yield points. `git diff develop --stat -- src/DataAcquisition.Core` prints nothing. |
| `feature/abs-pool-report` | #8 | Migration 0004 (three views), `docs/metrics.md`, static HTML with inline SVG and no JavaScript, CSV per trust, `report --out ./site`, Pages deploy gated on ingestion. |
| `feature/perf-gate` | #9 | 8 benchmarks, `tools/PerfGate`, the `perf-gate` job, `capture-baseline.yml`. **Shown failing in CI on a deliberate regression and passing after the revert.** |
| `feature/docs-and-runbook` | #10 | README with a Mermaid diagram, `docs/architecture.md`, `docs/adding-a-provider.md`, `docs/runbook.md`. Report generator tests closed a real coverage gap. |

**168 tests** (120 unit, 48 integration), 61.1% line coverage, `dotnet build -warnaserror`
clean. Measured numbers in [RESULTS.md](RESULTS.md); 19 decisions in
[docs/decisions.md](docs/decisions.md).

## Next
- Release `v1.0.0`: release PR, tag, GitHub Release, production run to Pages.
- Add `perf-gate` to `main`'s required status checks, now that the job exists on `main` (D1).
- Refresh `benchmarks/baseline.json` from a CI runner via `capture-baseline`, through a
  `chore/` branch into `develop` (D18).

## Open issues
- `benchmarks/baseline.json` is still a **local** capture (Apple M5) while CI runs on
  `ubuntu-latest`, so the gate's percentages on a CI run are not yet meaningful. Fixed by the
  baseline refresh above; the gate is report-only into `develop` until then.
- Coverage is uneven (Reports 88.2%, Storage 40.6%). What is uncovered is mostly the EDGAR and
  Treasury HTTP paths, which need live network calls, and DI registration.
- Cumulative net loss covers the periods ingested, not pool inception — the monthly filings do
  not state the balance at issuance. Stated on the report page itself.
