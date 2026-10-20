using DataAcquisition.Providers.SecAbsEe.Parsing;

namespace DataAcquisition.UnitTests.SecAbsEe;

/// <summary>
/// Parser tests against real trimmed exhibits. Expected values were read out of the fixture
/// files themselves, so they are the filer's numbers rather than the parser's.
/// </summary>
public sealed class AutoLoanXmlParserTests
{
    private static async Task<List<Providers.SecAbsEe.Model.AutoLoanRecord>> ParseAsync(string path, string cik)
    {
        var records = new List<Providers.SecAbsEe.Model.AutoLoanRecord>();
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(path, cik))
        {
            records.Add(record);
        }

        return records;
    }

    [Fact]
    public async Task Parses_every_record_in_the_pretty_printed_fixture()
    {
        var records = await ParseAsync(Fixtures.Gmcar, "2099048");
        Assert.Equal(200, records.Count);
    }

    [Fact]
    public async Task Parses_every_record_in_the_single_line_fixture()
    {
        // The real Exeter exhibit has no newlines at all. This is the regression guard
        // against anything line-oriented creeping back into the parser.
        Assert.DoesNotContain('\n', File.ReadAllText(Fixtures.Exeter));

        var records = await ParseAsync(Fixtures.Exeter, "2013062");
        Assert.Equal(200, records.Count);
    }

    [Fact]
    public async Task First_gmcar_record_matches_the_filing()
    {
        var record = (await ParseAsync(Fixtures.Gmcar, "2099048"))[0];

        Assert.Equal("2099048", record.TrustCik);
        Assert.Equal("0002099048 - 000002", record.AssetNumber);
        Assert.Equal(new DateOnly(2026, 8, 1), record.ReportingPeriodBegin);
        Assert.Equal(new DateOnly(2026, 8, 31), record.ReportingPeriodEnd);
        Assert.Equal(32_914.85m, record.OriginalLoanAmount);
        Assert.Equal(88, record.OriginalLoanTerm);
        Assert.Equal(867, record.ObligorCreditScore);
        Assert.Equal(3_527.33m, record.EndingBalance);
        Assert.Equal(10, record.RemainingTermToMaturity);
        Assert.Equal(0, record.CurrentDelinquencyDays);
        Assert.Equal(0m, record.ChargedOffPrincipalAmount);
        Assert.Equal(0m, record.RecoveredAmount);
        Assert.Null(record.ZeroBalanceCode);

        // A genuine 0% APR loan: subvented promotional financing, not missing data.
        Assert.Equal(0m, record.OriginalInterestRatePercentage);
        Assert.Equal(0m, record.ReportingPeriodInterestRatePercentage);
    }

    [Fact]
    public async Task First_exeter_record_matches_the_filing()
    {
        var record = (await ParseAsync(Fixtures.Exeter, "2013062"))[0];

        Assert.Equal("210298229996354301", record.AssetNumber);
        Assert.Equal(new DateOnly(2026, 8, 31), record.ReportingPeriodEnd);
        Assert.Equal(19_404.36m, record.OriginalLoanAmount);
        Assert.Equal(68, record.OriginalLoanTerm);
        Assert.Equal(517, record.ObligorCreditScore);
        Assert.Equal(0m, record.EndingBalance);

        // Rates are decimal fractions: 0.2702 is 27.02% APR, which is what a deep-subprime
        // pool prices at. Read as a literal percentage it would be 0.27%.
        Assert.Equal(0.2702m, record.OriginalInterestRatePercentage);

        // Paid off in May 2024 yet still reported in the 2026-08 file.
        Assert.Equal((short)1, record.ZeroBalanceCode);
        Assert.Equal(new DateOnly(2024, 5, 1), record.ZeroBalanceEffectiveDate);

        // Exeter omits recoveredAmount on all but 2.27% of records.
        Assert.Null(record.RecoveredAmount);
    }

    [Fact]
    public async Task Aggregates_over_the_gmcar_fixture_match_the_filing()
    {
        var records = await ParseAsync(Fixtures.Gmcar, "2099048");

        Assert.Equal(3_939_464.74m, records.Sum(r => r.EndingBalance ?? 0m));

        // currentDelinquencyStatus is optional: four of these two hundred omit it.
        Assert.Equal(196, records.Count(r => r.CurrentDelinquencyDays is not null));
        Assert.Equal(4, records.Count(r => r.CurrentDelinquencyDays is null));

        // Days past due, not bucket codes.
        Assert.Equal(10, records.Count(r => r.CurrentDelinquencyDays >= 30));

        Assert.Equal(4, records.Count(r => r.ZeroBalanceCode is not null));
        Assert.All(
            records.Where(r => r.ZeroBalanceCode is not null),
            r => Assert.Equal((short)1, r.ZeroBalanceCode));
    }

    [Fact]
    public async Task Non_numeric_credit_scores_become_null_instead_of_failing()
    {
        // obligorCreditScore is STRING_20_TYPE in the schema. The literal text "None"
        // appears three times in this fixture and 182 times in the full exhibit; int.Parse
        // would throw on every one.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            File.ReadAllText(Fixtures.Gmcar),
            "<obligorCreditScore>None</obligorCreditScore>").Count);

        var records = await ParseAsync(Fixtures.Gmcar, "2099048");

        Assert.Equal(3, records.Count(r => r.ObligorCreditScore is null));
        Assert.Equal(197, records.Count(r => r.ObligorCreditScore is not null));
    }

    [Fact]
    public async Task Aggregates_over_the_exeter_fixture_match_the_filing()
    {
        var records = await ParseAsync(Fixtures.Exeter, "2013062");

        Assert.Equal(167_275.92m, records.Sum(r => r.EndingBalance ?? 0m));

        // A seasoned deep-subprime pool: only ten of two hundred loans are still open, and
        // 190 carry a zero-balance code. Pool metrics have to filter on balance.
        Assert.Equal(10, records.Count(r => r.EndingBalance > 0m));
        Assert.Equal(190, records.Count(r => r.ZeroBalanceCode is not null));
        Assert.Equal(
            [(short)1, (short)4],
            records.Where(r => r.ZeroBalanceCode is not null)
                   .Select(r => r.ZeroBalanceCode!.Value)
                   .Distinct()
                   .Order());

        Assert.All(records, r => Assert.NotNull(r.CurrentDelinquencyDays));
    }

    [Fact]
    public async Task A_zero_credit_score_is_read_as_absent_not_as_a_score()
    {
        // Exeter reports 0 for 937 of 38,648 loans in the full exhibit, all typed
        // "Consumer Credit Bureau": a thin-file borrower with no score, not a borrower
        // scoring zero. Nine of them fall in this 200-record fixture. Kept as a score they
        // would drag the pool's weighted-average credit score toward zero.
        Assert.Equal(9, System.Text.RegularExpressions.Regex.Matches(
            File.ReadAllText(Fixtures.Exeter),
            "<obligorCreditScore>0</obligorCreditScore>").Count);

        var records = await ParseAsync(Fixtures.Exeter, "2013062");

        Assert.Equal(9, records.Count(r => r.ObligorCreditScore is null));
        Assert.DoesNotContain(records, r => r.ObligorCreditScore == 0);
    }

    [Theory]
    [InlineData("08-31-2026", 2026, 8, 31)]
    [InlineData("8-1-2026", 2026, 8, 1)]      // the schema's pattern allows single digits
    [InlineData("12-09-2025", 2025, 12, 9)]
    public void Dates_parse_in_both_schema_permitted_widths(string value, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), AutoLoanXmlParser.ParseDate(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-08-31")]   // ISO, which this element never uses
    [InlineData("31-08-2026")]   // day first
    [InlineData("None")]
    public void Unparseable_dates_become_null(string value)
    {
        Assert.Null(AutoLoanXmlParser.ParseDate(value));
    }

    [Fact]
    public void Zero_balance_dates_use_their_own_month_year_format()
    {
        // zeroBalanceEffectiveDate is DATE_MM_YYYY_TYPE while every other date in the same
        // file is DATE_TYPE.
        Assert.Equal(new DateOnly(2024, 5, 1), AutoLoanXmlParser.ParseMonthYear("05/2024"));
        Assert.Null(AutoLoanXmlParser.ParseMonthYear("05-2024"));
    }
}
