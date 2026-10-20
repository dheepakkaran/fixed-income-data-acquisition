using Microsoft.Extensions.Configuration;

namespace DataAcquisition.Core.Registry;

/// <summary>
/// Reads <c>Providers:&lt;Name&gt;:Enabled</c>. A provider with no entry is treated as
/// enabled, so registering a provider is enough to run it and configuration is only
/// needed to turn one off.
/// </summary>
/// <param name="configuration">Application configuration root.</param>
public sealed class ConfigurationProviderEnablement(IConfiguration configuration) : IProviderEnablement
{
    /// <summary>Configuration section holding per-provider settings.</summary>
    public const string SectionName = "Providers";

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    public bool IsEnabled(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var value = _configuration[$"{SectionName}:{providerName}:Enabled"];
        return string.IsNullOrWhiteSpace(value) || bool.Parse(value);
    }
}
