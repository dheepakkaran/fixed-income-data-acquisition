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
