# Adding a provider

Step by step, using the U.S. Treasury provider as the worked example — it was genuinely built
this way, on `feature/provider-treasury-yields`, and the proof that the framework held is one
command:

```bash
$ git diff develop --stat -- src/DataAcquisition.Core
$
```

Nothing. A new provider for a new asset class needed no change to the framework. In existing
files it touched **29 lines**: two in `Program.cs`, five of configuration, and project
references.

---

## 0. Research the source before writing any code

The single highest-value step, and the one most easily skipped. Download a real file, read the
official specification, and check the field names against the file rather than against memory.

For Treasury that meant fetching one year of the feed and finding:

- the payload sits in `<m:properties>` with `d:`-prefixed elements
  (`http://schemas.microsoft.com/ado/2007/08/dataservices`);
- the date is `d:NEW_DATE`, an `Edm.DateTime`;
- **`BC_30YEARDISPLAY` duplicates `BC_30YEAR`** — 15 `BC_` fields per day for 14 real tenors,
  and including it would double-count the long end of the curve;
- time-to-first-byte is **19.5 seconds**, because the endpoint generates a whole calendar year
  per request.

The last one changed the code: `AddStandardResilienceHandler`'s default 10-second attempt
timeout cut every attempt off, and the first run failed both sources with a timeout that had
nothing to do with the data. Had the research stopped at "the format looks like Atom", that
would have surfaced as a mystery in CI.

Each of the ABS-EE findings in [RESULTS.md section 5](../RESULTS.md) came out of this step too.

## 1. Define the record

One record per row you will store, holding **only the fields the report needs**. The ABS-EE
exhibit carries 67 elements and the parser keeps 16; parsing the rest would cost memory and
schema surface for no reader.

```csharp
public sealed record TreasuryParYield(
    DateOnly QuoteDate,
    decimal TenorMonths,     // months, because field names do not sort into curve order
    string TenorLabel,
    decimal RatePercent);    // PERCENTAGE here; ABS-EE rates are fractions
```

Document the units on the type. The two providers in this repository use **opposite rate
conventions**, and the comment is the first line of defence against a 100x error.

## 2. Write the options class

```csharp
public sealed class TreasuryYieldsOptions
{
    public const string SectionName = "Providers:TreasuryYields";

    public bool Enabled { get; set; } = true;
    public string UserAgent { get; set; } = "...";
    public int Years { get; set; } = 2;
}
```

Anything an operator might reasonably change belongs here rather than in code. Trust CIKs are
configuration for the same reason: adding a trust should not be a code change.

## 3. Write a streaming parser

`XmlReader` or an equivalent that does not hold the document. Two traps, both of which this
repository hit:

**Do not call `ReadAsync` after `ReadElementContentAsStringAsync` or `SkipAsync`.** Both
already advance the reader, so an extra read skips the next element. It cost this project
twice: the ABS-EE parser silently dropped `assetNumber` and produced **zero** records, and the
Treasury parser captured every other tenor and returned **1,400 points where 2,800 were due**.
Only the branch that consumes nothing should advance.

**Do not assume formatting.** One ABS-EE filer emits 2,138,467 lines, another a single 144 MB
line. Match fields by name, never by position or count: Exeter omits
`servicerAdvancedAmount` and `servicingFlatFeeAmount` entirely, 65 elements against 67.

```csharp
public static async IAsyncEnumerable<TreasuryParYield> ParseAsync(
    Stream stream, bool ownsStream = false,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
```

Set `DtdProcessing = DtdProcessing.Prohibit` and `XmlResolver = null`. These files come off
the internet and a DTD is an external-entity vector the schema never needs.

## 4. Write the validator

Rules in the shared categories (`required-field`, `value-range`, `duplicate-key`,
`date-format`) so counts are comparable across providers. Pick the severity deliberately:
`Error` abandons the load, `Warning` is counted and reported.

**Set thresholds from the data, not from intuition.** Exeter's deep-subprime pool really does
reach 29% APR, so a sane-range rule written from a prime-auto instinct would reject valid
filings. Keep a `Reset()` for duplicate-detection state, because the provider is a singleton
and a second run must not inherit the first run's keys.

## 5. Write the migration and the table map

A new file in `db/migrations/`, numbered after the last. Provider tables arrive with their
provider rather than up front.

```sql
create table if not exists treasury_par_yields (
    quote_date    date         not null,
    tenor_months  numeric(6,1) not null,   -- numeric: the feed publishes a 1.5-month point
    tenor_label   text         not null,
    rate_percent  numeric(10,4) not null,
    constraint treasury_par_yields_pk primary key (quote_date, tenor_months),
    constraint treasury_par_yields_rate_plausible check (rate_percent between -5 and 25)
);
```

The primary key **is** the natural key, which is what makes the upsert idempotent. Add check
constraints as a backstop: validation catches bad data first, but the constraint is what
catches a future bug in validation.

Then an `IPostgresTableMap<TRecord>` listing the columns in wire order, with `IsKey: true` on
the natural key, a `WriteRowAsync` for binary COPY and a `ToValues` for the INSERT baseline.

## 6. Subclass `DataProviderBase<TRecord>`

Four overrides and nothing else.

```csharp
public sealed class TreasuryYieldsProvider : DataProviderBase<TreasuryParYield>
{
    public override string Name => "TreasuryYields";          // the config key
    public override string AssetClass => AssetClasses.UsTreasuries;

    public override Task<IReadOnlyList<DataSource>> DiscoverAsync(...);
    public override Task<FetchedSource> FetchAsync(...);
    public override IAsyncEnumerable<TreasuryParYield> ParseAsync(...);
    public override Task<ValidationResult> ValidateAsync(...);
}
```

Optionally override `TryGetCachedHashAsync` so a repeat run skips without any request.
**Whether you should is a property of the source.** SEC does, because an exhibit is immutable
once filed. Treasury does not, because the current year's feed gains a row every business day
and a cached copy says nothing about whether upstream changed.

`AllowEmptySources` and `BatchSize` are the other hooks; the defaults are usually right.

## 7. Register it

```csharp
public static IServiceCollection AddTreasuryYieldsProvider(
    this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<TreasuryYieldsOptions>(options => { /* bind, env override */ });

    services.AddHttpClient<TreasuryYieldsProvider>((sp, client) =>
        {
            // TryAddWithoutValidation, not UserAgent.ParseAdd: "Name email@domain" is not a
            // valid RFC 9110 product token, and ParseAdd rejects exactly the form SEC asks for.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        })
        .AddStandardResilienceHandler(r => { /* timeouts from a measurement */ });

    services.TryAddSingleton<TreasuryYieldValidator>();
    services.AddCopyBulkLoader<TreasuryParYield, TreasuryParYieldTableMap>();
    services.AddSingleton<IDataProvider, TreasuryYieldsProvider>();
    return services;
}
```

Then two lines in `Program.cs` and a configuration block. That is the whole integration.

## 8. Add a fixture and tests

Trim a real download to a handful of records and commit it. Both ABS-EE shapes are in
`tests/fixtures/` on purpose, so the single-line case cannot regress.

**Read expected values out of the fixture**, so the test asserts the filer's numbers rather
than the parser's. Cover at least:

- the parser against the fixture, with known values;
- validation catching deliberately injected bad rows — **and** not flagging real quirks, which
  `Real_data_quirks_are_not_treated_as_errors` pins;
- ingestion idempotency: a second run is skipped, `--force` does not duplicate;
- an end-to-end load into a real PostgreSQL via Testcontainers.

**Cross-check the count against something independent.** The Treasury parser's dropped-tenor
bug was caught that way: 2,800 was expected from 200 business days × 14 tenors and the run
reported 1,400. No test was failing. Nothing looked wrong in the code.

## 9. Benchmark it

Add the parser to `ParserBenchmarks`. The gate then protects it, and the benchmark is also
where a performance assumption gets checked — the ABS-EE parser's 1 MiB read buffer turned out
to be half of its total allocation for about 3% of throughput.

## 10. Prove the framework held

```bash
git diff develop --stat -- src/DataAcquisition.Core
```

Nothing should print. If Core had to change, that is worth understanding and recording in
[decisions.md](decisions.md) — it usually means an abstraction is in the wrong place, not that
the provider is unusual.

---

## Checklist

- [ ] Real file downloaded, official spec read, field names checked against the file
- [ ] Record holds only what the report needs, with units documented
- [ ] Options class, with anything an operator might change
- [ ] Streaming parser; no read after a read that already advanced; fields matched by name
- [ ] `DtdProcessing.Prohibit`
- [ ] Validator with severities and thresholds taken from the data
- [ ] Migration with the natural key as the primary key, plus check constraints
- [ ] `IPostgresTableMap` in wire order
- [ ] `DataProviderBase` subclass, four overrides
- [ ] `TryGetCachedHashAsync` decided deliberately
- [ ] Registered; `Program.cs` and `appsettings.json` updated
- [ ] Fixture trimmed from a real download and committed
- [ ] Parser, validation, idempotency and end-to-end tests
- [ ] Parser benchmark added
- [ ] `git diff develop --stat -- src/DataAcquisition.Core` prints nothing
