namespace DataAcquisition.Providers.SecAbsEe.Model;

/// <summary>
/// One loan, for one reporting period, from an ABS-EE EX-102 asset data file.
/// <para>
/// Only the fields the ABS pool report needs are kept; the exhibit carries 67 elements and
/// parsing all of them would cost memory and schema surface for no reader. Field names come
/// from <c>eis_ABS_AutoLoanAssetData.xsd</c> in the EDGAR ABS XML Technical Specification
/// v3.1. Every element except <c>assetNumber</c> is <c>minOccurs="0"</c> in that schema and
/// real filings use the latitude, so all of these are nullable.
/// </para>
/// </summary>
public sealed record AutoLoanRecord
{
    /// <summary>Filing trust's CIK. Comes from the source, not the file.</summary>
    public required string TrustCik { get; init; }

    /// <summary>Item 3(a) <c>assetNumber</c>. Opaque: filers use very different formats.</summary>
    public required string AssetNumber { get; init; }

    /// <summary><c>reportingPeriodEndingDate</c>, the period this row describes.</summary>
    public DateOnly ReportingPeriodEnd { get; init; }

    /// <summary><c>reportingPeriodBeginningDate</c>.</summary>
    public DateOnly? ReportingPeriodBegin { get; init; }

    /// <summary><c>originalLoanAmount</c>, used as the original pool balance denominator.</summary>
    public decimal? OriginalLoanAmount { get; init; }

    /// <summary><c>originalLoanTerm</c> in months.</summary>
    public int? OriginalLoanTerm { get; init; }

    /// <summary><c>originalInterestRatePercentage</c>. Legitimately 0 for subvented promotional loans.</summary>
    public decimal? OriginalInterestRatePercentage { get; init; }

    /// <summary><c>reportingPeriodInterestRatePercentage</c>, the rate in force this period.</summary>
    public decimal? ReportingPeriodInterestRatePercentage { get; init; }

    /// <summary>
    /// <c>obligorCreditScore</c>. Declared <c>STRING_20_TYPE</c> in the schema, not an
    /// integer, so a non-numeric value is possible and is kept as null rather than failing.
    /// </summary>
    public int? ObligorCreditScore { get; init; }

    /// <summary><c>reportingPeriodBeginningLoanBalanceAmount</c>.</summary>
    public decimal? BeginningBalance { get; init; }

    /// <summary><c>reportingPeriodActualEndBalanceAmount</c>, the pool balance driver.</summary>
    public decimal? EndingBalance { get; init; }

    /// <summary><c>remainingTermToMaturityNumber</c> in months; drives the spread's tenor match.</summary>
    public int? RemainingTermToMaturity { get; init; }

    /// <summary>
    /// Item 3(f)(25) <c>currentDelinquencyStatus</c>: days past due, not a bucket code.
    /// Absent on a material share of records for some filers.
    /// </summary>
    public int? CurrentDelinquencyDays { get; init; }

    /// <summary><c>chargedoffPrincipalAmount</c>.</summary>
    public decimal? ChargedOffPrincipalAmount { get; init; }

    /// <summary><c>recoveredAmount</c>.</summary>
    public decimal? RecoveredAmount { get; init; }

    /// <summary>
    /// Item 3(f)(24)(ii) <c>zeroBalanceCode</c>: 1 Prepaid or Matured, 2 Third-party Sale,
    /// 3 Repurchased or Replaced, 4 Charged-off, 5 Servicing Transfer, 99 Unavailable.
    /// The schema allows the element to repeat; every filing seen carries at most one, and
    /// the parser keeps the first and records a warning if more appear.
    /// </summary>
    public short? ZeroBalanceCode { get; init; }

    /// <summary><c>zeroBalanceEffectiveDate</c>, formatted <c>MM/yyyy</c> unlike the other dates.</summary>
    public DateOnly? ZeroBalanceEffectiveDate { get; init; }

    /// <summary>
    /// How many <c>zeroBalanceCode</c> elements the record carried. Not persisted: it exists
    /// so validation can report a filing that uses the schema's <c>maxOccurs="unbounded"</c>
    /// rather than have the parser drop the extras silently.
    /// </summary>
    public int ZeroBalanceCodeCount { get; init; }
}
