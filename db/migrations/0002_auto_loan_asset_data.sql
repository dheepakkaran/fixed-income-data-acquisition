-- Loan-level auto loan ABS data from Form ABS-EE exhibit EX-102.
-- Field names and types follow eis_ABS_AutoLoanAssetData.xsd (EDGAR ABS XML Technical
-- Specification v3.1). Only the fields the pool report needs are stored.

create table if not exists auto_loan_asset_data (
    -- Natural key: a loan is identified by its trust, its reporting period and its asset
    -- number. assetNumber is the only minOccurs="1" element in the schema, and its format
    -- is entirely filer-specific ("0002099048 - 000002" at GM Financial,
    -- "210298229996354301" at Exeter), so it is stored as opaque text.
    trust_cik                      text    not null,
    reporting_period_end           date    not null,
    asset_number                   text    not null,

    reporting_period_begin         date,
    original_loan_amount           numeric(20,8),
    original_loan_term             integer,

    -- Rates are DECIMAL FRACTIONS, not percentages, despite the schema element being named
    -- originalInterestRatePercentage. Verified against real filings: GM Financial's prime
    -- pool runs 0.019 to 0.1415 and Exeter's deep-subprime pool 0.0995 to 0.29, i.e. 1.9%
    -- to 29% APR. Read as literal percentages those pools would price at 0.06% and 0.22%.
    -- Treasury par yields, by contrast, ARE percentages, so the spread metric converts.
    original_interest_rate         numeric(20,8),
    reporting_period_interest_rate numeric(20,8),

    -- obligorCreditScore is STRING_20_TYPE in the schema, not an integer, and real filings
    -- use that latitude: the literal text "None" appears 182 times in GM Financial's
    -- 2026-08 exhibit. Non-numeric values are stored as null.
    obligor_credit_score           integer,

    beginning_balance              numeric(20,8),
    ending_balance                 numeric(20,8),
    remaining_term_to_maturity     integer,

    -- Item 3(f)(25): days past due, not a bucket code (values up to 115 observed).
    current_delinquency_days       integer,

    charged_off_principal_amount   numeric(20,8),
    recovered_amount               numeric(20,8),

    -- Item 3(f)(24)(ii): 1 Prepaid or Matured, 2 Third-party Sale, 3 Repurchased or
    -- Replaced, 4 Charged-off, 5 Servicing Transfer, 99 Unavailable.
    zero_balance_code              smallint,
    zero_balance_effective_date    date,

    constraint auto_loan_asset_data_pk
        primary key (trust_cik, reporting_period_end, asset_number),
    constraint auto_loan_balance_non_negative
        check (ending_balance is null or ending_balance >= 0),
    constraint auto_loan_zero_balance_code_known
        check (zero_balance_code is null or zero_balance_code in (1, 2, 3, 4, 5, 99))
);

comment on table auto_loan_asset_data is
    'Loan-level auto loan ABS data, one row per loan per reporting period, from ABS-EE EX-102.';
comment on column auto_loan_asset_data.original_interest_rate is
    'Decimal fraction, not a percentage: 0.0639 means 6.39% APR.';
comment on column auto_loan_asset_data.current_delinquency_days is
    'Days past due (Item 3(f)(25)), so 30+/60+/90+ buckets are day thresholds.';

-- Closed loans stay in the file long after they pay off: in Exeter 2024-2 only 10 of the
-- first 200 records still carry a balance. Pool metrics filter on a positive ending
-- balance, so that filter gets its own index.
create index if not exists ix_auto_loan_active
    on auto_loan_asset_data (trust_cik, reporting_period_end)
    where ending_balance > 0;

-- Serves the payoff and charge-off rates, which read only the rows that closed.
create index if not exists ix_auto_loan_zero_balance
    on auto_loan_asset_data (trust_cik, reporting_period_end, zero_balance_code)
    where zero_balance_code is not null;
