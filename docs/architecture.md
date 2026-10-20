# Architecture

How the framework is put together, what flows through it, how a re-run stays idempotent, and
what happens when something fails.

---

## Components

Dependencies point inward. `Core` knows about no database, no HTTP client and no provider.

```
DataAcquisition.Core                 abstractions, the pipeline, run tracking
  ├── Abstractions/                  IDataProvider, IRecordSink, IIngestionRunStore, IRawFileCache
  ├── Model/                         DataSource, FetchedSource, IngestionRun, AssetClasses
  ├── Pipeline/                      DataProviderBase<TRecord>, IPipelineObserver
  ├── Registry/                      ProviderRegistry, IProviderEnablement
  └── Validation/                    ValidationResult, ValidationSeverity, ValidationRules

DataAcquisition.Storage              → Core
  ├── Migrations/                    DbUp runner, db/migrations embedded
  ├── Loading/                       CopyBulkLoader, InsertBulkLoader, IPostgresTableMap
  ├── Repositories/                  IngestionRunStore (Dapper)
  └── Caching/                       FileSystemRawFileCache

DataAcquisition.Providers.SecAbsEe         → Core, Storage
DataAcquisition.Providers.TreasuryYields   → Core, Storage
DataAcquisition.Reports                    → Core, Storage
DataAcquisition.Cli                        → everything above
```

**Core declares ports; Storage implements them.** `IRecordSink<TRecord>`,
`IIngestionRunStore` and `IRawFileCache` are interfaces in Core with no database or filesystem
types in their signatures. That is what lets the pipeline be unit tested with in-memory
doubles and keeps a provider from depending on Postgres directly.

**A provider is a subclass plus a registration.** `TreasuryYieldsProvider` was added without
changing a line of Core — `git diff develop --stat -- src/DataAcquisition.Core` printed
nothing on that branch. See [adding-a-provider.md](adding-a-provider.md).

---

## Data flow

`DataProviderBase<TRecord>.IngestAsync` is a template method. The provider supplies four
steps; the sequence, idempotency, batching, transaction boundaries and run accounting stay in
the base class.

```
discover ─▶ skip already-ingested ─▶ fetch (cache-first) ─▶ parse (streamed)
                                                              │
                                        ┌─────────────────────┘
                                        ▼
                             batch of BatchSize records
                                        │
                              validate ─┤── Error ─▶ abandon, roll back, run = Failed
                                        │
                                   stage (COPY)
                                        │
                        (stream exhausted) ─▶ merge + commit ─▶ record run = Succeeded
```

### 1. Discover

The provider lists what is available upstream as `DataSource` records: for SEC one EX-102
exhibit per accession, for Treasury one calendar year of the feed. A `DataSource` carries a
provider-scoped `SourceId`, the URI, the reporting period and the subject (trust CIK, or
`UST`).

### 2. Skip what is already ingested

Covered under [Idempotency](#idempotency) below.

### 3. Fetch, cache first

Bytes land in an on-disk cache keyed by the source id, which for SEC is the accession number
plus the document name. A download is written to a `.part` file and moved into place only when
complete, so an interrupted transfer never leaves a short file a later run would treat as
cached. Transfers are retried three times with exponential backoff and the delivered length is
checked against `Content-Length`.

### 4. Parse as a stream

`IDataProvider<TRecord>.ParseAsync` returns `IAsyncEnumerable<TRecord>` and implementations
must not hold the document. Measured: **1.2 MB of managed heap growth** parsing a 116.5 MiB
exhibit, so memory is a function of batch size rather than file size.

### 5. Validate in batches

The base class buffers `BatchSize` records (default 10,000), calls `ValidateAsync` on the
batch and merges the result. Two severities:

- **`Error`** — abandons the load, rolls the transaction back, marks the run `Failed`, and
  stops reading the rest of the file rather than parsing another hundred megabytes for a load
  already abandoned.
- **`Warning`** — counted and reported; the load proceeds.

Two severities rather than one because the ABS-EE schema marks every element except
`assetNumber` as optional and real filings use that latitude. One severity would force a
choice between failing every real file and never failing at all.

### 6. Load atomically

Each batch is streamed into an `on commit drop` temporary staging table with Npgsql binary
COPY. On commit, one `INSERT ... ON CONFLICT DO UPDATE` merges staging into the target. All
of it in one transaction, so a source lands whole or not at all.

### 7. Record the run

A row in `ingestion_runs` with status, counts, validation tallies, duration and the source
hash. The report cites these ids so any published figure traces to the file behind it.

---

## Idempotency

**A source's identity is `provider + source id + SHA-256 of the raw file`.**

The hash is what makes it content-based. An accession-number check alone would miss a trust
re-filing a corrected exhibit under the same accession number — the file changes, the name
does not.

The check runs in two places, because a content hash cannot exist before the content does:

1. **Before the fetch**, when the raw file is already cached. The hash comes from disk and a
   repeat run makes **no network request at all**, which is what SEC fair access asks for.
2. **Immediately after the fetch** otherwise. First sight of a source costs one download.

`--force` skips both, which is why the supporting index is deliberately **not unique**: a
forced re-ingest must be able to write a second succeeded row.

```sql
create index ix_ingestion_runs_identity
    on ingestion_runs (provider_name, source_id, sha256)
    where status = 1;   -- Succeeded only; the check never asks about anything else
```

The two providers differ here on purpose. SEC overrides `TryGetCachedHashAsync` because an
exhibit is immutable once filed. Treasury does not, because the current year's feed gains a
row every business day and a cached copy says nothing about whether upstream changed. Same
hook, opposite answers, no flag.

**Re-running is safe at the database too.** The target tables have natural keys —
`(trust_cik, reporting_period_end, asset_number)` and `(quote_date, tenor_months)` — and the
load upserts, so a re-run updates rows in place. `Re_running_a_load_does_not_duplicate_rows`
loads the same data three times and asserts 500 rows, not 1,500.

---

## Failure handling

| Failure | What happens |
|---|---|
| Transfer dies mid-body | Retried three times with backoff; the `.part` file is deleted. A fresh GET is the only recovery, because the headers were already accepted and part of the stream consumed — the resilience handler cannot replay that. |
| Transfer ends cleanly but short | Rejected by comparing the delivered length with `Content-Length`, so a silent truncation is not cached and hashed as the real exhibit. |
| Over SEC's rate ceiling | Cannot happen: requests are paced at one per 105 ms. SEC answers an over-rate client with **403**, not 429, which is easy to mistake for an authorisation problem, so a 403 is logged with that hint. |
| Validation error | Load abandoned, transaction rolled back, nothing committed, run `Failed` with the per-rule counts in `error_message`. Parsing stops immediately. |
| Source parses to **zero** records | Run `Failed`. An empty exhibit means the file, the format or the parser is wrong; reporting it as success hides the fault behind a green tick. |
| A source fails in the production run | The CLI exits non-zero, the `ingest` job fails, and the `deploy` job (`needs: ingest`) never runs. Nothing publishes. |
| Process dies mid-run | The run row is left `Running`. Nothing was committed, because the commit is the last step, so a re-run redoes that source cleanly. |
| Performance regresses | `perf-gate` fails the release PR. Enforcing into `main`, report-only into `develop`. |

**Fail loudly is a property of the job graph, not of step ordering.** The report is generated
in the `ingest` job and deployed from a separate `deploy` job that depends on it. A partial
ingest therefore cannot publish a page that looks current and is wrong.

---

## Configuration

Bound from `appsettings.json`, overridable by environment variables. Secrets are not needed:
both data sources are public.

| Key | Purpose |
|---|---|
| `Providers:<Name>:Enabled` | Switches a provider off. Absent means enabled, so registering a provider is enough to run it. |
| `Providers:SecAbsEe:Trusts[]` | Trust CIK, display name, credit tier. Adding a trust is configuration. |
| `Providers:SecAbsEe:UserAgent` | Declared to SEC. Overridable by `SEC_USER_AGENT` so a fork does not file requests under someone else's name. |
| `Providers:SecAbsEe:MaxRequestsPerSecond` | Capped at 10 in code; configuration cannot talk the provider into breaching fair access. |
| `Storage:ConnectionString` | Overridable by `FIDA_CONNECTION_STRING` for CI and the production run. |
| `Storage:CacheRoot` | Root of the on-disk raw file cache; one subdirectory per provider. |

---

## Schema

```
ingestion_runs          one row per attempt at one source; the audit trail the report cites
source_files            raw files seen upstream, keyed by content hash
auto_loan_asset_data    PK (trust_cik, reporting_period_end, asset_number)
treasury_par_yields     PK (quote_date, tenor_months)

v_pool_period           balances, counts, weighted averages, delinquency, payoff, period loss
v_pool_cumulative_loss  running net loss over the periods held
v_pool_spread           indicative spread against the nearest Treasury tenor
```

Provider tables arrive with their provider, in their own migration. Migrations 0001 and 0002
were written on different branches for exactly that reason.

Amounts are `numeric(20,8)` because ABS-EE's `DECIMAL_TYPE20_8` is 20 total digits with 8
fractional. Not `double`: these are money.
