using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;

namespace DataAcquisition.UnitTests.Fakes;

/// <summary>In-memory stand-in for the Dapper-backed run store.</summary>
public sealed class InMemoryRunStore : IIngestionRunStore
{
    private long _nextId;

    public List<IngestionRun> Started { get; } = [];

    public List<IngestionRun> Completed { get; } = [];

    /// <summary>provider|sourceId|sha256 triples treated as already ingested.</summary>
    public HashSet<string> AlreadyIngested { get; } = new(StringComparer.Ordinal);

    public Task<IngestionRun> StartAsync(IngestionRun run, CancellationToken cancellationToken)
    {
        var stored = run with { Id = Interlocked.Increment(ref _nextId) };
        Started.Add(stored);
        return Task.FromResult(stored);
    }

    public Task CompleteAsync(IngestionRun run, CancellationToken cancellationToken)
    {
        Completed.Add(run);
        return Task.CompletedTask;
    }

    public Task<bool> IsAlreadyIngestedAsync(
        string providerName,
        string sourceId,
        string sha256,
        CancellationToken cancellationToken) =>
        Task.FromResult(AlreadyIngested.Contains($"{providerName}|{sourceId}|{sha256}"));

    public Task<IReadOnlyList<IngestionRun>> GetRunsAsync(
        string? providerName,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IngestionRun>>(
            [.. Completed.Where(r => providerName is null || r.ProviderName == providerName).Take(limit)]);
}
