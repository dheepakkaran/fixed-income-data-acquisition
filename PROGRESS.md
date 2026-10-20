# PROGRESS

**Shipped.** `v1.0.0` is tagged, released and live.

- Repository: <https://github.com/dheepakkaran/fixed-income-data-acquisition>
- Live report: <https://dheepakkaran.github.io/fixed-income-data-acquisition/>
- Releases: [`v0.1.0`](https://github.com/dheepakkaran/fixed-income-data-acquisition/releases/tag/v0.1.0) ·
  [`v1.0.0`](https://github.com/dheepakkaran/fixed-income-data-acquisition/releases/tag/v1.0.0)

## Done

| Branch | PR | What |
|---|---|---|
| Setup (on `main`) | — | Solution skeleton, build config, `docker-compose.yml`, minimal `ci.yml`. Repo public, both branches protected, Pages enabled. |
| `feature/core-pipeline` | #1 | `IDataProvider`, `DataProviderBase` template method, `IngestionRun`, `ProviderRegistry`, `ValidationResult`. |
| `feature/storage-postgres` | #2 | DbUp migrations, `CopyBulkLoader`, `InsertBulkLoader` baseline, Dapper run store, raw-file cache. |
| `feature/provider-sec-abs-ee` | #3 | Streaming parser, EDGAR client, paced rate limiter, validator, migration 0002, CLI. **316,091 real records.** |
| `feature/release-workflows` | #4 | `release.yml`, `production-run.yml`. |
| **`v0.1.0`** | #5 | Tagged, released, production run from `main`. |
| `fix/production-run-reliability` | #6 | `pipefail`, download retry with a length check, a rate-limiter test that stopped measuring the thread pool. |
| `feature/provider-treasury-yields` | #7 | Second provider, second asset class, **zero Core changes**. 6,255 par yield points. |
| `feature/abs-pool-report` | #8 | Three SQL views, `docs/metrics.md`, static HTML with inline SVG, CSV per trust, Pages deploy gated on ingestion. |
| `feature/perf-gate` | #9 | 8 benchmarks, `tools/PerfGate`, the `perf-gate` job, `capture-baseline.yml`. Shown failing and passing in CI. |
| `feature/docs-and-runbook` | #10 | README with a Mermaid diagram, architecture, adding-a-provider, runbook. Report generator tests closed a coverage gap. |
| `chore/ci-perf-baseline` | #11 | Baseline captured on a CI runner; the gate now records and checks the machine. |
| `fix/perf-gate-thresholds` | #13 | Separate thresholds for time and memory, after two CPU models in one runner pool differed by 33%. |
| **`v1.0.0`** | #12 | Tagged, released, production run published to Pages. |
| `chore/final-results` | #14 | Final measured numbers. |

**175 tests** (127 unit, 48 integration), 61.1% line coverage, `dotnet build -warnaserror`
clean, 20 decisions recorded.

## Open issues

None blocking. Three things a reader should know, each stated on the report page itself:

- **Cumulative net loss covers the periods ingested, not pool inception.** The monthly filings
  never state the pool's balance at issuance.
- **The indicative spread is simplified** — a gross loan coupon less a nearest-tenor
  government yield, not a discount margin or a Z-spread.
- **Coverage is uneven** (Reports 88.2%, Storage 40.6%). What is uncovered is mostly the EDGAR
  and Treasury HTTP paths, which would need live network calls.

## If this were picked up again

- Refresh `benchmarks/baseline.json` after each release via the **Capture perf baseline**
  workflow, as `docs/runbook.md` describes. A baseline drifts.
- More trusts is configuration only: add an entry to `appsettings.json`.
- A third asset class would test the framework further than a second did — the two current
  providers are both XML over HTTP.
