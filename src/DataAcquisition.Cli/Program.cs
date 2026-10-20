using DataAcquisition.Cli.Commands;
using DataAcquisition.Core;
using DataAcquisition.Providers.SecAbsEe;
using DataAcquisition.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables("FIDA_");

builder.Logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });

builder.Services.AddDataAcquisitionCore();
builder.Services.AddDataAcquisitionStorage(builder.Configuration);
builder.Services.AddSecAbsEeProvider(builder.Configuration);

using var host = builder.Build();

var parsed = CommandLine.Parse(args);
if (parsed.Command is null)
{
    CommandLine.PrintUsage();
    return parsed.HelpRequested ? 0 : 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return parsed.Command switch
    {
        "migrate" => await MigrateCommand.RunAsync(host.Services, cancellation.Token),
        "ingest" => await IngestCommand.RunAsync(host.Services, parsed, cancellation.Token),
        "status" => await StatusCommand.RunAsync(host.Services, parsed, cancellation.Token),
        _ => Unknown(parsed.Command),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    CommandLine.PrintUsage();
    return 2;
}
