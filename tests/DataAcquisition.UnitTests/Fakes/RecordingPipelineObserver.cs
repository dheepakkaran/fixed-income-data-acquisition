using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;

namespace DataAcquisition.UnitTests.Fakes;

/// <summary>Captures the stage sequence so ordering can be asserted.</summary>
public sealed class RecordingPipelineObserver : IPipelineObserver
{
    private readonly List<PipelineStage> _stages = [];

    public IReadOnlyList<PipelineStage> Stages => _stages;

    public ValueTask StageAsync(
        string providerName,
        PipelineStage stage,
        DataSource? source,
        CancellationToken cancellationToken)
    {
        lock (_stages)
        {
            _stages.Add(stage);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stage sequence with consecutive repeats collapsed (batching repeats stages).</summary>
    public IReadOnlyList<PipelineStage> Distinct()
    {
        lock (_stages)
        {
            var result = new List<PipelineStage>();
            foreach (var stage in _stages)
            {
                if (result.Count == 0 || result[^1] != stage)
                {
                    result.Add(stage);
                }
            }

            return result;
        }
    }
}
