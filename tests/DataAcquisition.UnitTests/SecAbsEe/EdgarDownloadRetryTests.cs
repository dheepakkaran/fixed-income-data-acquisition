using System.Net;
using DataAcquisition.Core.Model;
using DataAcquisition.Providers.SecAbsEe.Http;
using DataAcquisition.Storage.Caching;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.UnitTests.SecAbsEe;

/// <summary>
/// A 150 MB transfer sometimes dies mid-body. The first production run hit exactly that and
/// lost one of nine sources, so recovery is tested rather than assumed.
/// </summary>
public sealed class EdgarDownloadRetryTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "fida-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    /// <summary>A stream that yields some bytes and then faults, like a dropped transfer.</summary>
    private sealed class TruncatingStream(byte[] payload, int failAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            }

            var toCopy = Math.Min(count, failAfter - _position);
            Array.Copy(payload, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FlakyHandler(byte[] payload, int failures) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;

            HttpContent content = Attempts <= failures
                ? new StreamContent(new TruncatingStream(payload, payload.Length / 2))
                : new ByteArrayContent(payload);

            content.Headers.ContentLength = payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private EdgarClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            new FileSystemRawFileCache(_cacheDirectory),
            NullLogger<EdgarClient>.Instance,
            retryBaseDelay: TimeSpan.FromMilliseconds(1));

    private static DataSource Source() => new(
        "000209904826000028-exh1024860082026.xml",
        new Uri("https://www.sec.gov/Archives/edgar/data/2099048/x/exh.xml"),
        new DateOnly(2026, 8, 31),
        "2099048",
        "GMCAR 2026-08");

    [Fact]
    public async Task A_truncated_transfer_is_retried_and_succeeds()
    {
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var handler = new FlakyHandler(payload, failures: 1);
        var fetched = await Client(handler).FetchExhibitAsync(Source(), "key-1", CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
        Assert.Equal(payload.Length, fetched.SizeBytes);
        Assert.False(fetched.FromCache);
        Assert.Equal(payload.Length, new FileInfo(fetched.LocalPath).Length);
    }

    [Fact]
    public async Task A_transfer_that_keeps_failing_gives_up_and_reports_it()
    {
        var payload = new byte[16 * 1024];
        var handler = new FlakyHandler(payload, failures: 99);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Client(handler).FetchExhibitAsync(Source(), "key-2", CancellationToken.None));

        Assert.Equal(3, handler.Attempts);

        // No partial file is left for a later run to mistake for a complete download.
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.part"));
    }

    [Fact]
    public async Task A_short_download_that_ends_cleanly_is_still_rejected()
    {
        // Content-Length says one thing and the body delivers another. Without this check a
        // silent truncation would be cached and surface later as a half-parsed exhibit.
        var payload = new byte[32 * 1024];

        var handler = new ShortHandler(payload, delivered: payload.Length / 2);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Client(handler).FetchExhibitAsync(Source(), "key-3", CancellationToken.None));

        Assert.Equal(3, handler.Attempts);
    }

    /// <summary>Delivers fewer bytes than its own Content-Length, without faulting.</summary>
    private sealed class ShortHandler(byte[] payload, int delivered) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            var content = new ByteArrayContent(payload, 0, delivered);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
