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

---

## D7 — The SEC rate limiter paces requests instead of windowing them

**Decision.** `SecRateLimitingHandler` uses a `TokenBucketRateLimiter` holding a single
token, replenished every `1000 / rate x 1.05` ms, rather than a sliding or fixed window.

**Why.** A window-based limiter does not hold the fair-access ceiling. Both fixed and
sliding windows grant the entire permit limit the moment a window opens, so ten requests at
the end of one window and ten at the start of the next satisfy the limiter while an observer
sliding a one-second window across the arrivals counts nineteen. That is not theoretical:
`Never_exceeds_ten_requests_in_any_one_second_window` measured 19 against a
`SlidingWindowRateLimiter` configured with a permit limit of 10. Segmenting a sliding window
smooths when permits come back, not how many may be spent at once.

A single-token bucket spaces requests at least one replenishment period apart, which bounds
every one-second span rather than just each window. The 5% margin exists because the
replenishment timer fires on a best-effort cadence: pacing at exactly 10/s let scheduler
slack put two requests 98 ms apart, which the same test saw as 11 in a second. The ceiling
is a limit to stay under, not a target to hit, and 5% of a few dozen requests costs nothing.

**Rejected.** Raising the measurement tolerance to make the window limiter's test pass. The
test is asserting the thing SEC actually cares about.

---

## D8 — A credit score of zero is read as "no score", not as a score

**Decision.** The parser maps `obligorCreditScore` of `0` to null.

**Why.** Exeter reports `0` for 937 of 38,648 loans, every one typed
`Consumer Credit Bureau`. That is what a thin-file or no-hit borrower looks like in a
deep-subprime pool, not a borrower who scored zero; no bureau scale reaches zero. Kept as a
score it drags the pool's weighted-average credit score down (589.6 against 592.4 on the
active pool) and raises 937 spurious validation warnings per period.

The schema cannot settle this, because it types the element as `STRING_20_TYPE` precisely so
filers can put non-numeric placeholders in it — GM Financial writes the literal text `None`
182 times in one exhibit. Both are the same statement in different spellings.

---

## D9 — A source that parses to zero records fails the run

**Decision.** `DataProviderBase` marks a run `Failed` when a source yields no records,
unless the provider overrides `AllowEmptySources`.

**Why.** Found by running the thing. A stale assembly left the parser returning nothing, and
all nine sources were reported `Succeeded` with "0 parsed, 0 loaded, no rule breaches": the
load committed nothing, validation had nothing to object to, and the run went green. An
ABS-EE exhibit with no assets in it means the file, the format or the parser is wrong, and
reporting that behind a green tick is worse than failing, because the production run would
have published an empty report without complaint.

---

## D10 — Large downloads are retried in the provider, not by the resilience handler

**Decision.** `EdgarClient.FetchExhibitAsync` retries a failed transfer itself, three
attempts with exponential backoff, and verifies the delivered length against
`Content-Length`.

**Why.** `AddStandardResilienceHandler` cannot help here, which the first production run
demonstrated: one of nine sources died with
`HttpIOException: The response ended prematurely` partway through a 120 MB body and the
handler passed it straight through as a failed source. Exhibits are fetched with
`HttpCompletionOption.ResponseHeadersRead` and streamed to disk, so by the time a transfer
dies its headers have been accepted and part of its stream consumed. Polly cannot replay
that. A fresh GET is the only recovery, so the whole download has to be the unit of retry.

The length check is there because a transfer can end cleanly and still be short. Without it
a silent truncation would be cached, hashed and treated as the real exhibit, surfacing much
later as a half-parsed file.

**Rejected.** Raising the resilience handler's retry count. It would retry the request, not
the transfer, and the failure mode it needs to cover happens after the retry decision has
already been made.

---

## D11 — The production run's ingest step sets `pipefail`

**Decision.** The ingest step is an explicit `bash` block beginning `set -o pipefail`.

**Why.** The step pipes the CLI through `tee` to capture a log, and a bash pipeline's exit
status is its last command's. Without `pipefail` the step's status was `tee`'s, which is
always zero. The first production run reported "8 succeeded, 0 skipped, 1 failed" and the
workflow went green. "Fail loudly on a validation failure" is a requirement, and a green
tick over a failed source is the precise opposite.

---

## D12 — The rate limiter's wall-clock test asserts average rate, not arrival distribution

**Decision.** `The_average_request_rate_never_exceeds_the_ceiling` divides requests by
elapsed time. The earlier version slid a one-second window across recorded arrival
timestamps.

**Why.** The stricter-looking assertion was the flakier one. Arrival timestamps are taken in
a continuation, so when a shared runner deschedules the thread pool the arrivals bunch
together even though the limiter paced the permits correctly: it observed 11 on a macOS
runner in the release workflow while passing locally and on every Linux run. It was
measuring the thread pool.

Average rate can only be pushed down by scheduler noise, never up, so it fails only when the
limiter is genuinely wrong. The no-burst guarantee it used to imply is now asserted
directly and deterministically by `The_bucket_holds_a_single_token_so_there_is_no_burst_allowance`,
with no clock involved at all.

---

## D13 — The Treasury provider does not answer the skip check from its cache

**Decision.** `TreasuryYieldsProvider` deliberately leaves `TryGetCachedHashAsync` alone,
unlike the SEC provider which overrides it.

**Why.** The two feeds differ in a way that matters for idempotency. An ABS-EE exhibit is
immutable once filed, so a cached copy's hash is a complete answer and a repeat run makes no
request at all. The Treasury feed is published one calendar year per request and gains a row
every business day, so a cached copy of the current year says nothing about whether the
upstream has changed. Answering from the cache there would freeze the current year at
whatever day it was first fetched.

The content hash still does its job after the fetch: a finished year hashes identically and
is skipped, and the current year's hash changes with each new business day so the additions
are picked up. The cost is one request per year per run, which for a feed with a 19-second
response is the right trade.

---

## D14 — Treasury's HTTP timeouts are set from a measurement, not from the default

**Decision.** The Treasury client's resilience handler uses a 90-second attempt timeout and
a 5-minute total, rather than `AddStandardResilienceHandler`'s defaults.

**Why.** The endpoint generates a whole calendar year of the curve per request and is slow
by nature: time-to-first-byte measured **19.5 seconds** for 2026 (310 KB), with and without
an explicit `Accept` header. The standard handler's 10-second attempt timeout cut every
attempt off, and the first run failed both sources with a timeout that had nothing to do
with the data. The numbers here are that measurement plus headroom, not a guess.

---

## D15 — Charge-offs are summed across periods, including the negatives

**Decision.** `net_loss` for a period is `SUM(chargedoffPrincipalAmount) - SUM(recoveredAmount)`,
and `cumulative_net_loss` is the running total of that. Negative charge-off amounts are summed
as-is, not filtered out.

**Why.** The schema cannot settle whether these fields are period flows or running totals: the
Item 3(f) fields all say "during the reporting period" while Item 3(i)(1) says only "the amount
of uncollected principal charged-off". The data settles it. One AmeriCredit loan reads
+18,975.81 at 2026-06-30, **−18,975.81** at 2026-07-31 with its balance restored and the
zero-balance code gone, then +18,975.81 again at 2026-08-31: charged off, reversed by the
servicer, charged off again. Summing the three periods gives +18,975.81, the correct net.
Per-period totals are also non-monotonic across periods, which a running total could not be.

Dropping the negatives would overstate losses by counting the reversed charge-off twice.

**Not claimed.** This is cumulative over the periods ingested, not since pool inception. The
monthly filings never state the pool's balance at issuance, and loans removed from a filing
take their history with them. The report says so on the page rather than implying a
since-inception figure.

---

## D16 — The Pages deployment is a separate job gated on ingestion

**Decision.** `production-run.yml` generates the report in the `ingest` job and deploys it
from a `deploy` job with `needs: ingest`.

**Why.** A failed source fails the ingest job, and a dependent job does not run. Generating
and deploying in one job would mean a partial ingest could still publish, and a report that
quietly covers eight of nine sources is worse than no new report: the page would look current
and be wrong. Separating them makes "nothing publishes unless every source loaded" a property
of the graph rather than of step ordering.

---

## D17 — The perf gate is enforcing on release PRs and report-only into `develop`

**Decision.** `perf-gate` fails the build on a pull request into `main` and reports without
failing on a pull request into `develop`. Threshold 15% on either mean time or allocated
memory.

**Why.** The gate's job is to stop a performance regression reaching production, and the only
pull request that reaches production is the release pull request into `main`. Blocking every
feature branch as well would buy very little and cost a lot, because of how the measurement
actually behaves.

The benchmarks run as a BenchmarkDotNet `ShortRun` (2 warmups, 5 iterations) on a shared
GitHub runner. That is a deliberate trade: a full run would take tens of minutes on every
release for precision the 15% threshold never uses. The price is variance, and a noisy gate
that blocks merges is one people learn to re-run until it passes — at which point it is worse
than no gate, because it looks like protection. Report-only on feature branches keeps the
number in front of whoever wrote the change, where it is useful, without teaching anyone to
ignore a red tick.

The same reasoning sets the threshold. 15% is well outside the run-to-run spread of a short
job on a shared runner but far inside anything that matters: the COPY path is 8.9x faster than
row-by-row INSERT, and a change that gave that advantage back would show as hundreds of
percent, not seventeen.

**Also:** a missing `benchmarks/baseline.json` is not a regression. The gate reports that
there is nothing to compare against and exits 0, because failing a pull request over an absent
file rather than a slow change would be a false alarm on the very first run.

**Rejected.** Enforcing everywhere with a wider threshold. A threshold loose enough to absorb
runner noise on every branch would be loose enough to let a real regression through on the one
branch that matters.

---

## D18 — The baseline is captured on a CI runner, not a developer machine

**Decision.** `benchmarks/baseline.json` is produced by the `capture-baseline` workflow
running on `ubuntu-latest` from `main`, and refreshed that way after each release. It is
uploaded as an artifact and committed through a `chore/` branch rather than pushed by the
workflow, because `main` is protected and a workflow pushing to it would bypass the review
the protection exists for.

**Why.** Found by getting it wrong. The first baseline was captured locally on an Apple M5
and compared against an `ubuntu-latest` runner. The gate then reported **all eight**
benchmarks as regressed, including `ParseTreasuryCurve` at **+211.7%** — a benchmark the
change under test barely touched, which measured −5.1% against the same baseline locally.

The gate was not wrong; the comparison was meaningless. A baseline and a run from different
machines measure the hardware, and the difference swamps anything the threshold is there to
catch. It also fails in the dangerous direction: the noise is positive, so everything looks
like a regression, and a gate that cries wolf on every release is one that gets bypassed.

**Consequence for reading the demonstration in RESULTS.md.** The deliberate regression's CI
numbers (+499% on the pretty-printed parser) are inflated by this, which is why the local
measurement against a local baseline (+203%) is reported alongside them. Both show the gate
catching the regression; only the local pair is a clean measurement of its size.

**The gate now detects this itself**, rather than relying on anyone reading this note. Each
baseline records the machine its measurements came from, taken from BenchmarkDotNet's
`HostEnvironmentInfo` in the report rather than from the environment of whatever process ran
`capture` — the first CI baseline was captured on a laptop from downloaded artifacts and duly
recorded the laptop. When the baseline's host differs from the current run's, `compare` prints
a GitHub warning annotation and puts a block at the top of the markdown naming both machines.

It warns rather than fails: the percentages are *meaningless*, not bad, and failing there would
block a release on a stale baseline instead of on a slow change.

For the record, the two machines differ by a factor of 2.2 to 2.8:

| Benchmark | Apple M5 | ubuntu-latest (Xeon 8370C) |
|---|---:|---:|
| `ParsePrettyPrinted` | 1.360 ms | 3.013 ms |
| `CopyLoad[Records=10000]` | 84.786 ms | 199.826 ms |
| `PoolPeriodQuery[Records=10000]` | 7.225 ms | 20.300 ms |

---

## D19 — A failed PR comment does not fail the perf gate

**Decision.** The `perf-gate` job declares `pull-requests: write`, and the comment step
carries `continue-on-error: true`.

**Why.** Both halves were learned from one red build. The job had no `permissions` block, so
it ran with the default restricted token and the comment step failed with
`GraphQL: Resource not accessible by integration (addComment)` — *after* the gate itself had
already passed. The job went red for a posting problem, which is the gate reporting something
it does not measure.

The permission fixes the cause. `continue-on-error` covers the rest: GitHub can refuse a
comment for reasons that have nothing to do with performance — a fork's token, a disabled
feature, an API outage — and the verdict is already carried by the step's exit code and the
job summary. A gate that goes red for reasons unrelated to performance is a gate that gets
ignored, which is the same failure mode D17 is written to avoid.
