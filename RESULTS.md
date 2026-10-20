# RESULTS

Every number here came from a run actually performed. Nothing is estimated or carried over
from documentation.

**Measured on:** Apple M5, 10 cores, 16 GB RAM, macOS 27.0 (arm64), .NET SDK 10.0.402,
PostgreSQL 16.15 in Docker (8 GB allocated to the VM). Release builds throughout.

---

## 1. Records ingested

Real SEC EDGAR Form ABS-EE exhibit EX-102 filings: 3 auto loan trusts across 3 reporting
periods each. One `ingest --periods 3` run, cache warm, from the CLI.

| Trust | CIK | Credit tier | Period | Loans | Still open | Pool balance |
|---|---|---|---|---:|---:|---:|
| GM Financial Consumer Auto 2026-1 | 2099048 | Prime | 2026-06-30 | 30,197 | 29,639 | $1,058.89 M |
| GM Financial Consumer Auto 2026-1 | 2099048 | Prime | 2026-07-31 | 29,653 | 29,134 | $1,022.63 M |
| GM Financial Consumer Auto 2026-1 | 2099048 | Prime | 2026-08-31 | 29,152 | 28,627 | $987.22 M |
| AmeriCredit Auto 2024-1 | 2020251 | Subprime | 2026-06-30 | 37,160 | 35,591 | $702.38 M |
| AmeriCredit Auto 2024-1 | 2020251 | Subprime | 2026-07-31 | 36,112 | 34,667 | $675.15 M |
| AmeriCredit Auto 2024-1 | 2020251 | Subprime | 2026-08-31 | 35,225 | 33,807 | $650.28 M |
| Exeter Auto 2024-2 | 2013062 | Deep subprime | 2026-06-30 | 39,972 | 21,141 | $358.19 M |
| Exeter Auto 2024-2 | 2013062 | Deep subprime | 2026-07-31 | 39,972 | 20,423 | $343.40 M |
| Exeter Auto 2024-2 | 2013062 | Deep subprime | 2026-08-31 | 38,648 | 19,759 | $329.70 M |

**Total: 316,091 loan-level records, 9 sources, 0 failed, 0 skipped.** Wall clock for the
whole run including discovery requests: **9.63 s**.

The "still open" column is why pool metrics filter on a positive ending balance. Closed
loans stay in the file long after they pay off, and in Exeter's seasoned deep-subprime pool
only 51% of reported loans still carry a balance, against 98% in GM Financial's.

## 2. Parser throughput and memory

`ParseThroughputTests.Streams_a_full_size_exhibit_with_flat_memory`, over the real
GM Financial 2026-08 exhibit rather than a fixture.

| Measure | Value |
|---|---|
| File | `000209904826000028-exh1024860082026.xml` |
| Size | 116.5 MiB (122,184,076 bytes) |
| Records | 29,152 |
| Elapsed | 0.78 s |
| Throughput | **37,263 records/s** |
| Throughput | **148.9 MB/s** |
| Managed heap growth across the parse | **1.2 MB** |
| Total allocated (cumulative, not peak) | 124.6 MB |

**Peak memory for the whole 9-source run** (`/usr/bin/time -l`, 316,091 records over files
up to 160 MB): maximum resident set size **158.6 MB**, peak memory footprint **100.5 MB**.

The 1.2 MB figure is the one that matters: live memory stayed at ~1% of file size, so a
116 MB exhibit and a 1 GB one cost the same. The test asserts heap growth stays under a
quarter of file size, which fails loudly if anything stops streaming.

## 3. COPY against row-by-row INSERT

`LoadStrategyComparisonTests.Binary_copy_beats_row_by_row_insert_on_the_same_data`. Same
records, same table, same batch sizes, same transaction semantics; only the write strategy
differs. Row shapes are parsed from a real exhibit and re-keyed up to the real record count.

| Strategy | Records | Elapsed | Rows/s |
|---|---:|---:|---:|
| Npgsql binary COPY into staging, then one upsert | 29,152 | **429 ms** | **68,009** |
| Row-by-row parameterised, prepared `INSERT` | 29,152 | 3,814 ms | 7,642 |

**Speedup: 8.9x.** At one month for one trust that is 3.4 seconds saved; across the nine
sources of a single production run it is about 30 seconds, and the gap widens linearly with
pool size. The test asserts COPY remains the faster path, so if that ever stops being true
the extra complexity of the staging table has stopped paying for itself and the build says so.

## 4. Tests and coverage

| Suite | Tests | Duration |
|---|---:|---:|
| `DataAcquisition.UnitTests` | 120 | 5 s |
| `DataAcquisition.IntegrationTests` (Testcontainers, real PostgreSQL 16) | 48 | 8 s |
| **Total** | **168** | |

**Line coverage: 61.1%** (2,686 of 4,394 coverable lines), as the union of both suites.
Counted as a union rather than by adding the two coverlet reports together: both measure the
same lines, so summing them reports a different and misleading figure.

| Assembly | Covered | |
|---|---:|---:|
| `DataAcquisition.Reports` | 1,027 / 1,164 | 88.2% |
| `DataAcquisition.Core` | 454 / 590 | 76.9% |
| `PerfGate` | 119 / 228 | 52.2% |
| `DataAcquisition.Providers.TreasuryYields` | 231 / 476 | 48.5% |
| `DataAcquisition.Providers.SecAbsEe` | 543 / 1,168 | 46.5% |
| `DataAcquisition.Storage` | 312 / 768 | 40.6% |

The spread is uneven on purpose. What is left uncovered in the provider and storage
assemblies is mostly the EDGAR and Treasury HTTP paths, which would need live network calls,
and DI registration. `Reports` was at 39.3% until `ReportGeneratorTests` was added: the metric
SQL had been tested directly against the views, which left the code that *reads* them
untested — a gap a view test cannot close.

## 5. Data quality found in the real filings

Each of these was found by reading actual downloads, and each would have produced a wrong
report or a crash if taken on trust from the schema alone.

| Finding | Evidence | Consequence |
|---|---|---|
| Interest rates are decimal fractions, not percentages, despite every element being named `...Percentage` | GM Financial prime pool 0.019-0.1415; Exeter deep subprime 0.0995-0.29 | Read as percentages a prime pool prices at 0.06% APR. Treasury par yields *are* percentages, so the spread metric converts |
| `obligorCreditScore` is `STRING_20_TYPE`, not an integer | the literal text `None` appears **182 times** in one GM Financial exhibit | `int.Parse` throws on 182 real records |
| A credit score of `0` is a sentinel for "no score", not a score | **937 of 38,648** Exeter loans, all typed `Consumer Credit Bureau` | Counted as a score it drags the pool's weighted-average credit score down (589.6 against 592.4 on the active pool) |
| `currentDelinquencyStatus` is optional and frequently absent | present on 98.13% of GM Financial records, 95.06% of AmeriCredit, 100% of Exeter | Delinquency rates need an explicit denominator |
| `currentDelinquencyStatus` is days past due, not a bucket code | values observed up to 115 | 30+/60+/90+ are day thresholds |
| `zeroBalanceCode` and `subvented` are `maxOccurs="unbounded"` | XSD lines 285 and 207; one record carries `<subvented>1</subvented><subvented>2</subvented>` | A scalar mapping silently keeps only the last |
| Exhibit formatting is entirely up to the filing agent | GM Financial 2026-08 is 2,138,467 lines; Exeter's is **one line of 144 MB** | Anything line-oriented either exhausts memory or depends on whitespace the schema never promised |
| Filers omit different optional elements altogether | Exeter omits `servicerAdvancedAmount` and `servicingFlatFeeAmount`; 65 elements against 67 | Order- or count-dependent parsing breaks between trusts |
| `www.sec.gov/Archives` ignores HTTP `Range` | sent `Range: bytes=0-120000`, received HTTP 200 and all 122 MB | Partial fetch is not available; idempotency cannot lean on conditional requests |
| 0% APR loans are real | 8 of the first 200 GM Financial loans, flagged `subvented` | A "rate must be positive" rule would reject valid data |

## 6. The performance gate, shown failing and then passing

Benchmarks run as a BenchmarkDotNet `ShortRun` (2 warmups, 5 iterations) with
`MemoryDiagnoser`. The gate compares mean time and allocated memory per benchmark against
`benchmarks/baseline.json` and fails past 15%.

### Baseline (Apple M5, local)

| Benchmark | Mean | Allocated |
|---|---:|---:|
| `ParserBenchmarks.ParsePrettyPrinted` | 1.360 ms | 2,024,232 B |
| `ParserBenchmarks.ParseSingleLine` | 1.203 ms | 2,038,754 B |
| `ParserBenchmarks.ParseAndAggregate` | 1.425 ms | 2,024,249 B |
| `ParserBenchmarks.ParseTreasuryCurve` | 0.068 ms | 210,004 B |
| `StorageBenchmarks.CopyLoad[Records=10000]` | 84.786 ms | 3,392,560 B |
| `StorageBenchmarks.PoolPeriodQuery[Records=10000]` | 7.225 ms | 4,344 B |
| `StorageBenchmarks.SpreadQuery[Records=10000]` | 4.090 ms | 3,168 B |
| `StorageBenchmarks.CumulativeLossQuery[Records=10000]` | 2.507 ms | 3,070 B |

### The regression the gate caught

A `Regex` constructed per call inside `ParseDecimal`, arriving disguised as extra validation.
All 16 parser correctness tests still passed, and `build-test` was green: this is precisely
the class of defect tests cannot see.

| Benchmark | Baseline | With the regression | Change |
|---|---:|---:|---:|
| `ParsePrettyPrinted` | 1.36 ms | **4.12 ms** | **+203.0% time, +205.8% memory** |
| `ParseSingleLine` | 1.20 ms | 3.57 ms | +197.3% time, +167.2% memory |
| `ParseAndAggregate` | 1.42 ms | 2.78 ms | +95.0% time, +205.8% memory |

**CI run with the regression: [failure](https://github.com/dheepakkaran/fixed-income-data-acquisition/actions/runs/39387182737)**
— `build-test: success`, `perf-gate: failure`, "Performance gate failed: 8 benchmark(s)
regressed by more than 15%."

### After the revert

| Benchmark | Mean | vs baseline | Allocated | vs baseline |
|---|---:|---:|---:|---:|
| `ParsePrettyPrinted` | 1.36 ms | −0.2% | 1.00 MB | **−48.2%** |
| `ParseSingleLine` | 1.11 ms | −7.8% | 1.01 MB | −47.9% |
| `ParseAndAggregate` | 1.36 ms | −4.7% | 1.00 MB | −48.2% |
| `ParseTreasuryCurve` | 61.76 us | −9.1% | 205.1 KB | +0.0% |

The allocation is down by half because of a separate finding. The first attempt at a
"deliberately slow" change shrank the parser's `FileStream` buffer from 1 MiB to 64 bytes —
and **it was not slow**: +3.6% time, −51% allocation. `XmlReader` buffers on top of the
stream, so the 1 MiB buffer was simply half of the parser's 2 MB allocation per parse, bought
for about 3% of throughput. It is now 64 KiB, which is the real change kept out of that
attempt.

### What the CI numbers also showed

In CI the regression read **+499%**, and *every* benchmark tripped — including
`ParseTreasuryCurve` at **+211.7%**, which the same change measured at **−5.1%** locally. The
baseline was captured on an Apple M5 and compared against an `ubuntu-latest` runner, so the
comparison was measuring the hardware. The gate was right to fail; the comparison was not
meaningful. Hence `capture-baseline.yml`, which captures the baseline on a CI runner from
`main` (decision D18), and why the local pair above is reported alongside the CI result: both
show the gate working, only the local pair measures the regression's actual size.

## 7. Rate limiting

SEC fair access permits at most 10 requests per second.

A `SlidingWindowRateLimiter` with a permit limit of 10 **does not hold that ceiling**: it
grants the whole limit the instant a window opens, so ten requests at the end of one window
and ten at the start of the next both satisfy the limiter while an observer sliding a
one-second window across the arrivals counts **19**. `SecRateLimitingHandlerTests
.Never_exceeds_ten_requests_in_any_one_second_window` measured exactly that.

Replaced with an evenly paced single-token bucket, replenished every
`1000 / rate x 1.05` ms. The 5% margin is there because the replenishment timer fires on a
best-effort cadence, and pacing at exactly the ceiling let ordinary scheduler slack put two
requests 98 ms apart, which the same test then saw as 11 in a second. With the margin the
observed maximum is 10, stable across repeated runs.

