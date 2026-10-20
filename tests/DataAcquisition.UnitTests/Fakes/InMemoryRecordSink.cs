using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;

namespace DataAcquisition.UnitTests.Fakes;

/// <summary>
/// Stand-in for the COPY-based loader. Staged rows become committed only on
/// <see cref="Session.CommitAsync"/>, mirroring the real transaction boundary.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
public sealed class InMemoryRecordSink<TRecord> : IRecordSink<TRecord>
{
    public List<TRecord> Committed { get; } = [];

    public List<TRecord> Staged { get; } = [];

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public Task<IRecordSinkSession<TRecord>> BeginAsync(
        DataSource source,
        CancellationToken cancellationToken) =>
        Task.FromResult<IRecordSinkSession<TRecord>>(new Session(this));

    private sealed class Session(InMemoryRecordSink<TRecord> owner) : IRecordSinkSession<TRecord>
    {
        private readonly List<TRecord> _pending = [];
        private bool _committed;

        public Task WriteAsync(IReadOnlyList<TRecord> batch, CancellationToken cancellationToken)
        {
            _pending.AddRange(batch);
            owner.Staged.AddRange(batch);
            return Task.CompletedTask;
        }

        public Task<long> CommitAsync(CancellationToken cancellationToken)
        {
            owner.Committed.AddRange(_pending);
            owner.CommitCount++;
            _committed = true;
            return Task.FromResult((long)_pending.Count);
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                owner.RollbackCount++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
