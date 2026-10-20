namespace DataAcquisition.Storage;

/// <summary>Storage and cache configuration, bound from the <c>Storage</c> section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Npgsql connection string. Overridable by the <c>FIDA_CONNECTION_STRING</c> environment variable.</summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=55432;Database=fida;Username=fida;Password=fida_local_dev";

    /// <summary>
    /// Root of the on-disk raw-file cache. Each provider gets a subdirectory, so a cached
    /// exhibit is re-read instead of re-downloaded.
    /// </summary>
    public string CacheRoot { get; set; } = ".cache";
}
