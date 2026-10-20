using DataAcquisition.Core.Abstractions;

namespace DataAcquisition.Core.Registry;

/// <summary>
/// The set of providers the host knows about. Adding a provider is a registration plus a
/// configuration entry; neither this class nor anything else in Core changes.
/// </summary>
/// <param name="providers">Every provider registered with the container.</param>
/// <param name="enablement">Per-provider on/off switch.</param>
public sealed class ProviderRegistry(
    IEnumerable<IDataProvider> providers,
    IProviderEnablement enablement)
{
    private readonly IReadOnlyList<IDataProvider> _all =
        [.. (providers ?? throw new ArgumentNullException(nameof(providers)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    private readonly IProviderEnablement _enablement =
        enablement ?? throw new ArgumentNullException(nameof(enablement));

    /// <summary>Every registered provider, enabled or not.</summary>
    public IReadOnlyList<IDataProvider> All => _all;

    /// <summary>Providers configuration leaves switched on.</summary>
    public IReadOnlyList<IDataProvider> Enabled =>
        [.. _all.Where(p => _enablement.IsEnabled(p.Name))];

    /// <summary>Distinct asset classes the enabled providers cover.</summary>
    public IReadOnlyList<string> AssetClasses =>
        [.. Enabled.Select(p => p.AssetClass).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public bool TryGet(string name, out IDataProvider provider)
    {
        provider = _all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return provider is not null;
    }

    public IDataProvider Get(string name) =>
        TryGet(name, out var provider)
            ? provider
            : throw new KeyNotFoundException(
                $"No provider named '{name}'. Registered: {string.Join(", ", _all.Select(p => p.Name))}");
}
