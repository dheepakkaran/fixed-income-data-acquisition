using DataAcquisition.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Storage.Caching;

/// <summary>Hands each provider a cache rooted in its own directory.</summary>
public sealed class RawFileCacheFactory(IOptions<StorageOptions> options)
{
    private readonly StorageOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public IRawFileCache ForProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return new FileSystemRawFileCache(Path.Combine(_options.CacheRoot, providerName));
    }
}
