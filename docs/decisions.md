# Decisions

Design choices that are not obvious from the code, each with the alternative that was
rejected. New dependencies outside the approved stack must be justified here first.

---

## D1 — `perf-gate` is not a required check on `main` until the workflow exists

**Decision.** Branch protection on `main` initially requires only `build-test`.
`perf-gate` is added to the required list in `feature/perf-gate`.

**Why.** GitHub blocks a PR until every required check reports. A check name that no
workflow produces never reports, so requiring `perf-gate` from day one would have left
the `v0.1.0` release PR permanently unmergeable. The end state is the one asked for:
both checks are required on `main` from `feature/perf-gate` onward.

---

## D2 — Validation has two severities, not one

**Decision.** `ValidationSeverity` distinguishes `Error` (abandons the load, run marked
`Failed`) from `Warning` (counted and reported, load proceeds).

**Why.** The SEC ABS-EE auto loan schema marks every element except `assetNumber` as
`minOccurs="0"`. Real filings use that latitude: in GM Financial Consumer Automobile
Receivables Trust 2026-1 for period 2026-08-31, `currentDelinquencyStatus` is present on
only 98.13% of 29,152 records and `reportingPeriodInterestRatePercentage` on 99.94%.
Genuine 0% APR loans also exist (subvented promotional financing). A single severity
would force a choice between failing every real file or never failing at all.

**Rejected.** A tolerance threshold ("fail above N% bad rows"). It hides which rule broke
and needs tuning per provider; severity is a property of the rule and needs none.

---

## D3 — The already-ingested check straddles the fetch

**Decision.** Source identity is provider + source id + SHA-256 of the raw file. The skip
check runs *before* the fetch when the raw file is already in the on-disk cache, and
*again immediately after* the fetch otherwise.

**Why.** Content-based identity is what makes a re-filed exhibit under the same accession
number count as new work, which an accession-number-only check would miss. But a content
hash cannot exist before the content does. The split keeps the common case free: a repeat
run over cached files makes no network request at all, which is what SEC fair-access
guidance asks for. First sight of a source still costs one download.

**Rejected.** Trusting HTTP `ETag`/`Last-Modified`. `www.sec.gov/Archives` also ignores
`Range` requests (verified: `Range: bytes=0-120000` returned HTTP 200 and all 122 MB), so
its conditional-request behaviour is not something to build idempotency on.

---

## D4 — Validation and loading are batched, not whole-file

**Decision.** `ValidateAsync` takes a batch; `DataProviderBase` streams parse output into
batches of `BatchSize` (default 10,000), validates each, stages each, and commits once.

**Why.** Keeps peak memory a function of batch size rather than file size, so a 150 MB
exhibit and a 1 GB one cost the same. Staging into a temporary table before a single
`INSERT ... ON CONFLICT` keeps the whole source atomic despite the many batches.

**Rejected.** Validating the materialised record list. A 29,152-record file would fit, but
the design would stop working exactly when it matters.

---

## D5 — Postgres is published on host port 55432

**Decision.** `docker-compose.yml` maps `55432:5432`.

**Why.** 5432 is frequently already bound by a locally installed Postgres. A non-default
host port means `docker compose up` works on a machine that already runs one, and the
container port stays standard so nothing inside changes.

---

## D6 — Solution file is `.slnx`

**Decision.** The .NET 10 SDK's `dotnet new sln` produces the XML `.slnx` format and it is
kept.

**Why.** It is the SDK default, `dotnet build`/`restore`/`test` consume it directly, and
hand-editing it is far easier than the legacy `.sln` format. No tooling in this repo reads
the solution file other than the .NET CLI.
