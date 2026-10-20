using DataAcquisition.Core.Abstractions;

namespace DataAcquisition.Storage.Loading;

/// <summary>
/// A write strategy for parsed records. Implements Core's <see cref="IRecordSink{TRecord}"/>
/// so the pipeline is indifferent to which one is wired, and names itself so benchmarks and
/// RESULTS.md can report COPY against the row-by-row INSERT baseline on the same data.
/// </summary>
/// <typeparam name="TRecord">Record type being written.</typeparam>
public interface IBulkLoader<TRecord> : IRecordSink<TRecord>
{
    /// <summary>Short strategy name, used in logs and benchmark output.</summary>
    string Strategy { get; }
}
