namespace DataAcquisition.Core.Registry;

/// <summary>
/// Decides whether a registered provider participates in a run. Separated from the
/// registry so the registry is testable without building a configuration root.
/// </summary>
public interface IProviderEnablement
{
    bool IsEnabled(string providerName);
}
