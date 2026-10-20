namespace DataAcquisition.Core.Model;

/// <summary>
/// A <see cref="DataSource"/> whose bytes are now on local disk.
/// </summary>
/// <param name="Source">The source that was fetched.</param>
/// <param name="LocalPath">Path to the cached raw file.</param>
/// <param name="Sha256">
/// Lowercase hex SHA-256 of the raw bytes. Part of the run identity, so a refiled
/// exhibit with the same accession number is still recognised as new content.
/// </param>
/// <param name="SizeBytes">Size of the raw file on disk.</param>
/// <param name="FromCache">True when the bytes came from the on-disk cache, not the network.</param>
public sealed record FetchedSource(
    DataSource Source,
    string LocalPath,
    string Sha256,
    long SizeBytes,
    bool FromCache);
