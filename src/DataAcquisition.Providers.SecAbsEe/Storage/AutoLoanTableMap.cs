using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Storage.Loading;
using Npgsql;
using NpgsqlTypes;

namespace DataAcquisition.Providers.SecAbsEe.Storage;

/// <summary>
/// Maps <see cref="AutoLoanRecord"/> onto <c>auto_loan_asset_data</c>, created by
/// migration <c>0002_auto_loan_asset_data.sql</c>.
/// </summary>
public sealed class AutoLoanTableMap : IPostgresTableMap<AutoLoanRecord>
{
    public string TargetTable => "auto_loan_asset_data";

    public IReadOnlyList<PostgresColumn> Columns { get; } =
    [
        new("trust_cik", NpgsqlDbType.Text, IsKey: true),
        new("reporting_period_end", NpgsqlDbType.Date, IsKey: true),
        new("asset_number", NpgsqlDbType.Text, IsKey: true),
        new("reporting_period_begin", NpgsqlDbType.Date),
        new("original_loan_amount", NpgsqlDbType.Numeric),
        new("original_loan_term", NpgsqlDbType.Integer),
        new("original_interest_rate", NpgsqlDbType.Numeric),
        new("reporting_period_interest_rate", NpgsqlDbType.Numeric),
        new("obligor_credit_score", NpgsqlDbType.Integer),
        new("beginning_balance", NpgsqlDbType.Numeric),
        new("ending_balance", NpgsqlDbType.Numeric),
        new("remaining_term_to_maturity", NpgsqlDbType.Integer),
        new("current_delinquency_days", NpgsqlDbType.Integer),
        new("charged_off_principal_amount", NpgsqlDbType.Numeric),
        new("recovered_amount", NpgsqlDbType.Numeric),
        new("zero_balance_code", NpgsqlDbType.Smallint),
        new("zero_balance_effective_date", NpgsqlDbType.Date),
    ];

    public async ValueTask WriteRowAsync(
        NpgsqlBinaryImporter importer,
        AutoLoanRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await importer.WriteAsync(record.TrustCik, NpgsqlDbType.Text, cancellationToken);
        await importer.WriteAsync(record.ReportingPeriodEnd, NpgsqlDbType.Date, cancellationToken);
        await importer.WriteAsync(record.AssetNumber, NpgsqlDbType.Text, cancellationToken);

        await WriteAsync(importer, record.ReportingPeriodBegin, NpgsqlDbType.Date, cancellationToken);
        await WriteAsync(importer, record.OriginalLoanAmount, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.OriginalLoanTerm, NpgsqlDbType.Integer, cancellationToken);
        await WriteAsync(importer, record.OriginalInterestRatePercentage, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.ReportingPeriodInterestRatePercentage, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.ObligorCreditScore, NpgsqlDbType.Integer, cancellationToken);
        await WriteAsync(importer, record.BeginningBalance, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.EndingBalance, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.RemainingTermToMaturity, NpgsqlDbType.Integer, cancellationToken);
        await WriteAsync(importer, record.CurrentDelinquencyDays, NpgsqlDbType.Integer, cancellationToken);
        await WriteAsync(importer, record.ChargedOffPrincipalAmount, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.RecoveredAmount, NpgsqlDbType.Numeric, cancellationToken);
        await WriteAsync(importer, record.ZeroBalanceCode, NpgsqlDbType.Smallint, cancellationToken);
        await WriteAsync(importer, record.ZeroBalanceEffectiveDate, NpgsqlDbType.Date, cancellationToken);
    }

    public object?[] ToValues(AutoLoanRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return
        [
            record.TrustCik,
            record.ReportingPeriodEnd,
            record.AssetNumber,
            record.ReportingPeriodBegin,
            record.OriginalLoanAmount,
            record.OriginalLoanTerm,
            record.OriginalInterestRatePercentage,
            record.ReportingPeriodInterestRatePercentage,
            record.ObligorCreditScore,
            record.BeginningBalance,
            record.EndingBalance,
            record.RemainingTermToMaturity,
            record.CurrentDelinquencyDays,
            record.ChargedOffPrincipalAmount,
            record.RecoveredAmount,
            record.ZeroBalanceCode,
            record.ZeroBalanceEffectiveDate,
        ];
    }

    /// <summary>
    /// Write a nullable value, or a COPY null. Almost every ABS-EE element is optional, so
    /// this is the common case rather than an edge case.
    /// </summary>
    private static async ValueTask WriteAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
        where T : struct
    {
        if (value is { } present)
        {
            await importer.WriteAsync(present, dbType, cancellationToken);
        }
        else
        {
            await importer.WriteNullAsync(cancellationToken);
        }
    }
}
