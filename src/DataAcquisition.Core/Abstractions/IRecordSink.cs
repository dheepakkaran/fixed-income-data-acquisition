using DataAcquisition.Core.Model;

namespace DataAcquisition.Core.Abstractions;

/// <summary>
/// Where parsed records are written. Core declares the port and Storage implements it
/// with Npgsql binary COPY, so Core carries no database dependency.
/// </summary>
/// <typeparam name="TRecord">Record type being written.</typeparam>
public interface IRecordSink<TRecord>
{
    /// <summary>
    /// Open a load scope for one source. Nothing is visible in the target tables until
    /// <see cref="IRecordSinkSession{TRecord}.CommitAsync"/> is called.
    /// </summary>
    Task<IRecordSinkSession<TRecord>> BeginAsync(
        DataSource source,
        CancellationToken cancellationToken);
}

/// <summary>
/// One atomic load. Disposing without committing rolls the whole source back, which is
/// what the pipeline does when validation fails partway through a file.
/// </summary>
/// <typeparam name="TRecord">Record type being written.</typeparam>
public interface IRecordSinkSession<TRecord> : IAsyncDisposable
{
    /// <summary>Stage one batch. Safe to call many times before committing.</summary>
    Task WriteAsync(IReadOnlyList<TRecord> batch, CancellationToken cancellationToken);

    /// <summary>Merge everything staged into the target tables and commit.</summary>
    /// <returns>Rows inserted or updated in the target table.</returns>
    Task<long> CommitAsync(CancellationToken cancellationToken);
}
