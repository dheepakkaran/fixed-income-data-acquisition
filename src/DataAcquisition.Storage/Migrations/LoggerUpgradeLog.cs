using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Storage.Migrations;

/// <summary>
/// Routes DbUp's own output into <see cref="ILogger"/> so migration progress lands in the
/// same structured log as the rest of an ingestion run rather than on raw stdout.
/// </summary>
internal sealed class LoggerUpgradeLog(ILogger logger) : IUpgradeLog
{
    private readonly ILogger _logger = logger;

#pragma warning disable CA2254 // DbUp supplies the format string; it is not a compile-time constant here.
    public void LogTrace(string format, params object[] args) => _logger.LogTrace(format, args);

    public void LogDebug(string format, params object[] args) => _logger.LogDebug(format, args);

    public void LogInformation(string format, params object[] args) => _logger.LogInformation(format, args);

    public void LogWarning(string format, params object[] args) => _logger.LogWarning(format, args);

    public void LogError(string format, params object[] args) => _logger.LogError(format, args);

    public void LogError(Exception ex, string format, params object[] args) =>
        _logger.LogError(ex, format, args);
#pragma warning restore CA2254
}
