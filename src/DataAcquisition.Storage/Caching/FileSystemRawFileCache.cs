using System.Security.Cryptography;
using DataAcquisition.Core.Abstractions;

namespace DataAcquisition.Storage.Caching;

/// <summary>
/// Raw-file cache on the local filesystem, one directory per provider.
/// Downloads are written to a <c>.part</c> file and moved into place only once complete,
/// so an interrupted transfer never leaves a short file that a later run would mistake
/// for a cached one.
/// </summary>
/// <param name="directory">Directory this cache owns.</param>
public sealed class FileSystemRawFileCache(string directory) : IRawFileCache
{
    private const int BufferSize = 1 << 20;

    private readonly string _directory = string.IsNullOrWhiteSpace(directory)
        ? throw new ArgumentException("Cache directory is required.", nameof(directory))
        : directory;

    public string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_directory, Sanitize(key));
    }

    public bool TryGet(string key, out string path)
    {
        path = PathFor(key);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public async Task<(string Path, string Sha256)> WriteAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = PathFor(key);
        Directory.CreateDirectory(_directory);
        var partial = path + ".part";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using (var destination = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A truncated transfer must not leave a .part file behind for the next attempt
            // to trip over. The final path is never written, so the cache stays consistent.
            TryDelete(partial);
            throw;
        }

        File.Move(partial, path, overwrite: true);
        return (path, Convert.ToHexStringLower(hash.GetCurrentHash()));
    }

    public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover .part is harmless, it is never read.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Keep a provider-supplied key from escaping its own directory.</summary>
    private static string Sanitize(string key)
    {
        Span<char> buffer = key.Length <= 256 ? stackalloc char[key.Length] : new char[key.Length];
        var invalid = Path.GetInvalidFileNameChars();

        for (var i = 0; i < key.Length; i++)
        {
            buffer[i] = invalid.Contains(key[i]) ? '_' : key[i];
        }

        return new string(buffer);
    }
}
