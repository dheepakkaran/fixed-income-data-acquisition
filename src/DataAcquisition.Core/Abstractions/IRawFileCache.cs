namespace DataAcquisition.Core.Abstractions;

/// <summary>
/// On-disk cache of fetched raw files, keyed by a provider-scoped key (the accession
/// number for ABS-EE). Required by SEC fair-access guidance: an unchanged exhibit is
/// downloaded once, not once per run.
/// </summary>
public interface IRawFileCache
{
    /// <summary>Local path for <paramref name="key"/>, whether or not it exists yet.</summary>
    string PathFor(string key);

    /// <summary>True when a non-empty cached file exists for <paramref name="key"/>.</summary>
    bool TryGet(string key, out string path);

    /// <summary>
    /// Stream <paramref name="content"/> to the cache, writing to a temporary file first
    /// and moving it into place so an interrupted download never leaves a short file
    /// that a later run would treat as complete.
    /// </summary>
    /// <returns>The cached file's path and the lowercase hex SHA-256 of its bytes.</returns>
    Task<(string Path, string Sha256)> WriteAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>SHA-256 of an already-cached file.</summary>
    Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken);
}
