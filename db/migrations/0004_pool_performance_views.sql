-- ABS pool performance metrics, per trust and per reporting period.
--
-- Every formula here is written out in docs/metrics.md with the exact ABS-EE field names it
-- uses. Three conventions, each established by reading real filings, govern all of it:
--
--  1. Loan rates are DECIMAL FRACTIONS (0.0639 = 6.39% APR) while Treasury par yields are
--     PERCENTAGES (4.19 = 4.19%). Rates are multiplied by 100 here so every published
--     figure is a percentage.
--  2. Closed loans stay in the file long after they pay off. Only loans with a positive
--     ending balance are in the pool, which matters enormously for a seasoned pool: in
--     Exeter 2024-2 only 51% of reported loans still carry a balance, against 98% in GM
--     Financial's.
--  3. chargedoffPrincipalAmount is a per-period flow and a reversal is reported as a
--     NEGATIVE amount. One AmeriCredit loan reads +18,975.81, then -18,975.81 when the
--     servicer reversed the charge-off, then +18,975.81 when it was charged off again.
--     Filtering the negatives out would overstate losses, so they are summed as-is.

create or replace view v_pool_period as
select
    a.trust_cik,
    a.reporting_period_end,

    count(*)                                             as loans_reported,
    count(*) filter (where a.ending_balance > 0)          as loans_active,
    coalesce(sum(a.ending_balance), 0)                    as pool_balance,
    coalesce(sum(a.beginning_balance), 0)                 as pool_balance_opening,

    -- Denominator for cumulative loss: the original amount of every loan this period's file
    -- reports, open or closed. Not the pool's balance at issuance, which the monthly files
    -- do not state; loans removed from the file entirely are therefore not represented.
    coalesce(sum(a.original_loan_amount), 0)              as original_pool_balance,

    -- Balance-weighted averages over the active pool only.
    sum(a.ending_balance * a.reporting_period_interest_rate)
        filter (where a.ending_balance > 0 and a.reporting_period_interest_rate is not null)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.reporting_period_interest_rate is not null), 0)
      * 100                                               as wa_interest_rate_percent,

    -- Credit scores are excluded where absent rather than treated as zero. The parser
    -- already maps the "no score" sentinel of 0, and non-numeric values such as the literal
    -- text "None", to null.
    sum(a.ending_balance * a.obligor_credit_score)
        filter (where a.ending_balance > 0 and a.obligor_credit_score is not null)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.obligor_credit_score is not null), 0)
                                                          as wa_credit_score,

    sum(a.ending_balance * a.remaining_term_to_maturity)
        filter (where a.ending_balance > 0 and a.remaining_term_to_maturity is not null)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.remaining_term_to_maturity is not null), 0)
                                                          as wa_remaining_term_months,

    -- Delinquency by balance. currentDelinquencyStatus is DAYS past due (Item 3(f)(25)),
    -- not a bucket code, so these are day thresholds. The denominator is the active pool
    -- that actually reports a status: the element is optional and absent on up to 5% of
    -- records for some filers, and treating absent as current would flatter the numbers.
    coalesce(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days >= 30), 0)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days is not null), 0)
      * 100                                               as dq_30_plus_percent,

    coalesce(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days >= 60), 0)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days is not null), 0)
      * 100                                               as dq_60_plus_percent,

    coalesce(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days >= 90), 0)
      / nullif(sum(a.ending_balance)
        filter (where a.ending_balance > 0 and a.current_delinquency_days is not null), 0)
      * 100                                               as dq_90_plus_percent,

    count(*) filter (where a.ending_balance > 0 and a.current_delinquency_days is null)
                                                          as active_without_delinquency_status,

    -- Monthly payoff rate from zero-balance code 1 (Prepaid or Matured), measured on the
    -- balance those loans carried at the START of the period, over the whole opening
    -- balance. Using the ending balance would measure zero, since a paid-off loan ends at
    -- zero by definition.
    count(*) filter (where a.zero_balance_code = 1)        as loans_paid_off,
    coalesce(sum(a.beginning_balance) filter (where a.zero_balance_code = 1), 0)
      / nullif(sum(a.beginning_balance), 0) * 100         as payoff_rate_percent,

    count(*) filter (where a.zero_balance_code = 4)        as loans_charged_off,

    -- Period flow, reversals included as negatives.
    coalesce(sum(a.charged_off_principal_amount), 0)      as charged_off_principal,
    coalesce(sum(a.recovered_amount), 0)                  as recovered,
    coalesce(sum(a.charged_off_principal_amount), 0)
      - coalesce(sum(a.recovered_amount), 0)              as net_loss

from auto_loan_asset_data a
group by a.trust_cik, a.reporting_period_end;

comment on view v_pool_period is
    'Per-trust, per-period pool metrics. Rates are percentages; averages are balance-weighted over loans with a positive ending balance.';


-- Cumulative net loss, as a running total over the reporting periods ingested.
--
-- This is NOT loss since pool inception. The monthly files do not state the pool's balance
-- at issuance, and only the ingested periods contribute, so the figure is explicitly
-- "cumulative over the periods held". The report labels it that way.
create or replace view v_pool_cumulative_loss as
select
    p.trust_cik,
    p.reporting_period_end,
    p.net_loss,
    sum(p.net_loss) over (
        partition by p.trust_cik
        order by p.reporting_period_end
        rows between unbounded preceding and current row
    )                                                     as cumulative_net_loss,
    p.original_pool_balance,
    sum(p.net_loss) over (
        partition by p.trust_cik
        order by p.reporting_period_end
        rows between unbounded preceding and current row
    ) / nullif(p.original_pool_balance, 0) * 100          as cumulative_net_loss_percent,
    count(*) over (partition by p.trust_cik)              as periods_held
from v_pool_period p;

comment on view v_pool_cumulative_loss is
    'Running net loss over the ingested periods only, not since pool inception.';


-- Indicative spread: the pool's weighted-average interest rate less the Treasury par yield
-- at the tenor closest to the pool's weighted-average remaining term.
--
-- INDICATIVE AND SIMPLIFIED. It is not a bond-equivalent spread, a discount margin or a
-- Z-spread. It ignores the capital structure, servicing and excess-spread economics, the
-- shape of the curve between tenors, and credit enhancement. It compares a gross loan
-- coupon with a government yield, which is a rough proxy for the gross credit and
-- prepayment premium of the collateral and nothing more.
create or replace view v_pool_spread as
select
    p.trust_cik,
    p.reporting_period_end,
    p.wa_interest_rate_percent,
    p.wa_remaining_term_months,
    c.quote_date                                          as treasury_quote_date,
    t.tenor_label                                         as treasury_tenor,
    t.tenor_months                                        as treasury_tenor_months,
    t.rate_percent                                        as treasury_rate_percent,
    p.wa_interest_rate_percent - t.rate_percent           as indicative_spread_percent
from v_pool_period p

-- The latest published curve on or before the period end. A month end can fall on a
-- weekend or a holiday, when Treasury publishes nothing, so an equality join would silently
-- drop those periods.
left join lateral (
    select max(y.quote_date) as quote_date
    from treasury_par_yields y
    where y.quote_date <= p.reporting_period_end
) c on true

-- The tenor nearest the pool's weighted-average remaining term. Nearest rather than
-- interpolated: interpolation would imply a precision this metric does not have.
left join lateral (
    select y.tenor_label, y.tenor_months, y.rate_percent
    from treasury_par_yields y
    where y.quote_date = c.quote_date
      and p.wa_remaining_term_months is not null
    order by abs(y.tenor_months - p.wa_remaining_term_months), y.tenor_months
    limit 1
) t on true;

comment on view v_pool_spread is
    'Indicative, simplified spread: gross pool coupon less the nearest-tenor Treasury par yield. Not a discount margin or Z-spread.';
