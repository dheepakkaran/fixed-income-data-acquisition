using DataAcquisition.Core.Model;

namespace DataAcquisition.Core.Pipeline;

/// <summary>
/// Notified as the template method advances. Exists so the stage sequence is observable
/// from outside the base class — the ordering test asserts against it, and a future
/// metrics sink can hang off the same hook.
/// </summary>
public interface IPipelineObserver
{
    ValueTask StageAsync(
        string providerName,
        PipelineStage stage,
        DataSource? source,
        CancellationToken cancellationToken);
}

/// <summary>Default observer: does nothing.</summary>
public sealed class NullPipelineObserver : IPipelineObserver
{
    public static readonly NullPipelineObserver Instance = new();

    public ValueTask StageAsync(
        string providerName,
        PipelineStage stage,
        DataSource? source,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
