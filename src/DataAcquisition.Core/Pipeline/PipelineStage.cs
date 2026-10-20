namespace DataAcquisition.Core.Pipeline;

/// <summary>Steps of the ingestion template method, in the order the base class runs them.</summary>
public enum PipelineStage
{
    Discover = 0,
    Skip = 1,
    Fetch = 2,
    Parse = 3,
    Validate = 4,
    Load = 5,
    Record = 6,
}
