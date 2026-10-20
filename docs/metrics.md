# Metrics

Every published figure, the formula behind it, and the exact ABS-EE or Treasury field names
it reads. Implemented in `db/migrations/0004_pool_performance_views.sql`.

---

## Conventions that govern everything below

These three were established by reading real filings, and each one silently corrupts the
report if assumed wrong.

### 1. Loan rates are decimal fractions; Treasury yields are percentages

| Source | Field | Convention | Example |
|---|---|---|---|
| ABS-EE | `originalInterestRatePercentage`, `reportingPeriodInterestRatePercentage` | **decimal fraction** | `0.0639` = 6.39% APR |
| Treasury | `BC_2YEAR` and the other `BC_*` fields | **percentage** | `4.19` = 4.19% |

The ABS-EE element names say "Percentage" and are not percentages. Verified against filings:
GM Financial's prime pool runs 0.019–0.1415 and Exeter's deep-subprime pool 0.0995–0.29, i.e.
1.9%–14.15% and 9.95%–29% APR. Read as literal percentages a prime auto pool would price at
0.06%, which is impossible.

**Every rate in `v_pool_period` is multiplied by 100**, so all published figures and the
spread are percentages.

### 2. Only loans with a positive ending balance are in the pool

Closed loans stay in the file long after they pay off: `zeroBalanceEffectiveDate` is "the
date on which the loan balance was reduced to zero" (Item 3(f)(24)(i)), and one Exeter loan
reports `05/2024` in the 2026-08 file. The effect is large and varies by pool:

| Trust | 2026-08 loans reported | Still carrying a balance |
|---|---:|---:|
| GM Financial Consumer Auto 2026-1 | 29,152 | 28,627 (98%) |
| AmeriCredit Auto 2024-1 | 35,225 | 33,807 (96%) |
| Exeter Auto 2024-2 | 38,648 | 19,759 (51%) |

Balance-weighted averages and delinquency rates are therefore computed over
`reportingPeriodActualEndBalanceAmount > 0` only. Counting Exeter's closed half would halve
its apparent rate and score.

### 3. `currentDelinquencyStatus` is days, and it is optional

Item 3(f)(25): "the number of days the obligor is delinquent past the obligor's payment due
date". Days, not a bucket code — values up to 115 observed. So 30+/60+/90+ are day
thresholds.

It is also `minOccurs="0"` and genuinely absent: present on 98.13% of GM Financial's records,
95.06% of AmeriCredit's and 100% of Exeter's. **The delinquency denominator is the active
balance that actually reports a status**, not the whole active pool, because treating absent
as current would flatter the numbers. `v_pool_period.active_without_delinquency_status`
publishes how many were excluded.

---

## Pool size and composition

### Pool balance
```
pool_balance = SUM(reportingPeriodActualEndBalanceAmount)
```
Over every loan the period reports. Closed loans contribute zero, so no filter is needed.

### Loan count
```
loans_reported = COUNT(*)
loans_active   = COUNT(*) WHERE reportingPeriodActualEndBalanceAmount > 0
```
Both are published: `loans_reported` is what the filing contains, `loans_active` is the pool.

### Original pool balance
```
original_pool_balance = SUM(originalLoanAmount)
```
Over every loan the period reports, open or closed. **This is not the pool's balance at
issuance.** The monthly files do not state that, and loans removed from the file entirely are
not represented — Exeter's reported count falls from 39,972 to 38,648 between 2026-06 and
2026-08, and those 1,324 loans take their original amounts with them. Used only as the
cumulative-loss denominator, and labelled accordingly.

---

## Weighted averages

All weighted by ending balance, over the active pool. A loan missing the measured field is
excluded from both numerator and denominator rather than treated as zero.

### Weighted-average interest rate
```
wa_interest_rate_percent =
    SUM(reportingPeriodActualEndBalanceAmount * reportingPeriodInterestRatePercentage)
  / SUM(reportingPeriodActualEndBalanceAmount)
  * 100
```
over loans where `reportingPeriodActualEndBalanceAmount > 0`
and `reportingPeriodInterestRatePercentage IS NOT NULL`.

The `reportingPeriod` rate rather than the `original` rate: it is the rate in force during
the period, which is what the pool actually earns. The element is absent on 0.06%–1.58% of
records depending on filer.

### Weighted-average credit score
```
wa_credit_score =
    SUM(reportingPeriodActualEndBalanceAmount * obligorCreditScore)
  / SUM(reportingPeriodActualEndBalanceAmount)
```
over loans where `reportingPeriodActualEndBalanceAmount > 0`
and `obligorCreditScore IS NOT NULL`.

No `* 100`: a score is not a rate. Nulls are excluded, and the parser has already mapped two
kinds of non-score to null — the literal text `None` (182 occurrences in one GM Financial
exhibit, because the element is `STRING_20_TYPE`) and the sentinel `0` (937 of 38,648 Exeter
loans, all typed `Consumer Credit Bureau`, i.e. thin-file borrowers). Treating `0` as a score
moves Exeter's active-pool average from 592.4 to 589.6.

### Weighted-average remaining term
```
wa_remaining_term_months =
    SUM(reportingPeriodActualEndBalanceAmount * remainingTermToMaturityNumber)
  / SUM(reportingPeriodActualEndBalanceAmount)
```
over the active pool where the term is present. Feeds the spread's tenor match.

---

## Delinquency

```
dq_30_plus_percent =
    SUM(reportingPeriodActualEndBalanceAmount) WHERE currentDelinquencyStatus >= 30
  / SUM(reportingPeriodActualEndBalanceAmount) WHERE currentDelinquencyStatus IS NOT NULL
  * 100
```
Both sums restricted to `reportingPeriodActualEndBalanceAmount > 0`. `dq_60_plus_percent` and
`dq_90_plus_percent` are identical with thresholds of 60 and 90.

By balance rather than by count, which is the market convention and the measure that matters
to a noteholder. The buckets are cumulative and nested: a loan 95 days past due counts in all
three.

---

## Payoff rate

```
payoff_rate_percent =
    SUM(reportingPeriodBeginningLoanBalanceAmount) WHERE zeroBalanceCode = 1
  / SUM(reportingPeriodBeginningLoanBalanceAmount)
  * 100
```

`zeroBalanceCode = 1` is "Prepaid or Matured" (Item 3(f)(24)(ii); the full enumeration is
1 Prepaid or Matured, 2 Third-party Sale, 3 Repurchased or Replaced, 4 Charged-off,
5 Servicing Transfer, 99 Unavailable).

**Measured on the opening balance, not the ending balance.** A loan that paid off ends the
period at zero by definition, so an ending-balance numerator would always be zero. The
opening balance is what left the pool.

A monthly rate, not annualised. No filter on ending balance: the loans being counted are
precisely the ones that closed.

---

## Cumulative net loss

```
net_loss (period)      = SUM(chargedoffPrincipalAmount) - SUM(recoveredAmount)
cumulative_net_loss    = running SUM(net_loss) over reporting_period_end, per trust
cumulative_net_loss_%  = cumulative_net_loss / original_pool_balance * 100
```

`chargedoffPrincipalAmount` is Item 3(i)(1), "the amount of uncollected principal
charged-off"; `recoveredAmount` is Item 3(i)(2), "any amounts received after charge-off".

**Both are per-period flows, and a reversal arrives as a negative.** Established from the
data, not from the schema, which carries no period qualifier on either field (unlike the Item
3(f) fields, which all say "during the reporting period"). One AmeriCredit loan reads:

| Period | `chargedoffPrincipalAmount` | `reportingPeriodActualEndBalanceAmount` | `zeroBalanceCode` |
|---|---:|---:|---:|
| 2026-06-30 | +18,975.81 | 0.00 | 4 |
| 2026-07-31 | **−18,975.81** | 18,975.81 | *(absent)* |
| 2026-08-31 | +18,975.81 | 0.00 | 4 |

The servicer charged the loan off, reversed it, then charged it off again. Summing the three
periods gives +18,975.81, the correct net. **Negatives are therefore summed as-is**: filtering
them out would overstate losses. Per-period totals are also non-monotonic across periods
(Exeter: 6.29M, 7.06M, 6.73M), which a cumulative-to-date field could not be.

**This is cumulative over the reporting periods ingested, not since pool inception.** With
three months held the figures are three months of loss over a denominator that approximates
the original pool, and the report says so on the page.

---

## Indicative spread

```
indicative_spread_percent = wa_interest_rate_percent - treasury_rate_percent
```

where `treasury_rate_percent` is taken from `treasury_par_yields` as:

- **Curve date** — the latest `quote_date <= reporting_period_end`. A month end can fall on a
  weekend or holiday when Treasury publishes nothing, so an equality join would silently drop
  those periods.
- **Tenor** — the `tenor_months` nearest to `wa_remaining_term_months`, by absolute
  difference, ties broken toward the shorter tenor. Nearest rather than interpolated:
  interpolating would imply a precision this metric does not have.

Both legs are percentages (see convention 1), so the subtraction is valid as written.

### This metric is indicative and simplified

Labelled as such everywhere it appears. It is **not** a bond-equivalent spread, a discount
margin, or a Z-spread, and it is not what a trader would quote. It ignores:

- the capital structure — this is collateral yield, not any tranche's yield
- servicing fees, excess spread and the residual
- credit enhancement, overcollateralisation and reserve accounts
- the shape of the curve between the two nearest tenors
- expected losses and prepayment, which is most of why a deep-subprime pool yields 22%

It compares a gross loan coupon against a government yield of similar tenor. That is a rough
proxy for the gross credit and prepayment premium of the collateral, and nothing more.

---

## Data freshness and provenance

Every section of the report cites the `ingestion_runs.id` values behind it, with the run's
`completed_at` as the freshness stamp, so any published number can be traced to the file it
came from — provider, source id and the SHA-256 of the raw exhibit.
