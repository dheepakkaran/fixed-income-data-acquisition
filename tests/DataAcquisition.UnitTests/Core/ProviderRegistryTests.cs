using DataAcquisition.Core;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Registry;
using DataAcquisition.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.UnitTests.Core;

public sealed class ProviderRegistryTests
{
    private static ServiceProvider BuildHost(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddDataAcquisitionCore();

        // A provider registers itself. Core is not edited, recompiled or configured
        // with any knowledge of WidgetRecord.
        services.AddSingleton<IIngestionRunStore, InMemoryRunStore>();
        services.AddSingleton<IRecordSink<WidgetRecord>, InMemoryRecordSink<WidgetRecord>>();
        services.AddSingleton<IDataProvider>(sp => new FakeWidgetProvider(
            sp.GetRequiredService<IIngestionRunStore>(),
            sp.GetRequiredService<IRecordSink<WidgetRecord>>(),
            sp.GetRequiredService<ILogger<FakeWidgetProvider>>(),
            [],
            []));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_new_provider_resolves_through_di_with_no_core_changes()
    {
        using var host = BuildHost([]);

        var registry = host.GetRequiredService<ProviderRegistry>();

        var provider = Assert.Single(registry.All);
        Assert.Equal("FakeWidgets", provider.Name);
        Assert.Equal("Widgets", provider.AssetClass);
        Assert.Equal(["Widgets"], registry.AssetClasses);
    }

    [Fact]
    public void Provider_with_no_configuration_entry_is_enabled()
    {
        using var host = BuildHost([]);

        Assert.Single(host.GetRequiredService<ProviderRegistry>().Enabled);
    }

    [Fact]
    public void Provider_is_switched_off_by_configuration()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Providers:FakeWidgets:Enabled"] = "false",
        });

        var registry = host.GetRequiredService<ProviderRegistry>();

        Assert.Single(registry.All);
        Assert.Empty(registry.Enabled);
        Assert.Empty(registry.AssetClasses);
    }

    [Fact]
    public void Get_is_case_insensitive_and_reports_unknown_names()
    {
        using var host = BuildHost([]);
        var registry = host.GetRequiredService<ProviderRegistry>();

        Assert.Equal("FakeWidgets", registry.Get("fakewidgets").Name);

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("Nope"));
        Assert.Contains("FakeWidgets", ex.Message, StringComparison.Ordinal);
    }
}
