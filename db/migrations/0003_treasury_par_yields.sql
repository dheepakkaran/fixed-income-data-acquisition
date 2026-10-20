-- U.S. Treasury daily par yield curve rates, the benchmark leg of the indicative spread.

create table if not exists treasury_par_yields (
    -- Natural key: one rate per business day per tenor.
    quote_date    date           not null,
    tenor_months  numeric(6,1)   not null,

    tenor_label   text           not null,

    -- Treasury publishes par yields as PERCENTAGES: 4.19 means 4.19%. ABS-EE loan rates
    -- are decimal fractions (0.0639 means 6.39%), so the spread metric converts one of
    -- them. Mixing the two scales is a 100x error.
    rate_percent  numeric(10,4)  not null,

    constraint treasury_par_yields_pk primary key (quote_date, tenor_months),
    constraint treasury_par_yields_rate_plausible
        check (rate_percent between -5 and 25),
    constraint treasury_par_yields_tenor_positive
        check (tenor_months > 0)
);

comment on table treasury_par_yields is
    'Daily par yield curve from home.treasury.gov, one row per business day per tenor.';
comment on column treasury_par_yields.rate_percent is
    'Percentage, not a fraction: 4.19 means 4.19%. ABS-EE loan rates use the opposite convention.';
comment on column treasury_par_yields.tenor_months is
    'Tenor in months. Numeric because the feed publishes a 1.5-month point (BC_1_5MONTH).';

-- Serves the spread metric, which needs the curve as at a pool's period end.
create index if not exists ix_treasury_par_yields_date
    on treasury_par_yields (quote_date desc, tenor_months);
