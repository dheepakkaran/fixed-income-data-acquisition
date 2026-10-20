using System.Diagnostics;
using DataAcquisition.Providers.SecAbsEe.Parsing;
using Xunit.Abstractions;

namespace DataAcquisition.IntegrationTests.SecAbsEe;

/// <summary>
/// Parser throughput and memory on a genuinely full-size exhibit, for RESULTS.md.
/// <para>
/// Reads the real file out of the on-disk cache when it is there and skips otherwise, so a
/// clean checkout and CI are unaffected: a 122 MB exhibit does not belong in the repository,
/// and the numbers are only meaningful on the real article anyway.
/// </para>
/// </summary>
public sealed class ParseThroughputTests(ITestOutputHelper output)
{
    private static string? FullExhibit()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".cache", "SecAbsEe", "000209904826000028-exh1024860082026.xml");

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    [Fact]
    public async Task Streams_a_full_size_exhibit_with_flat_memory()
    {
        if (FullExhibit() is not { } path)
        {
            output.WriteLine("Cached exhibit absent; run `dotnet run --project src/DataAcquisition.Cli -- ingest` first.");
            return;
        }

        var sizeBytes = new FileInfo(path).Length;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var stopwatch = Stopwatch.StartNew();
        var records = 0L;
        var balance = 0m;

        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(path, "2099048"))
        {
            records++;
            balance += record.EndingBalance ?? 0m;
        }

        stopwatch.Stop();

        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var live = GC.GetTotalMemory(forceFullCollection: true) - before;

        output.WriteLine($"file                : {Path.GetFileName(path)}");
        output.WriteLine($"size                : {sizeBytes / 1024.0 / 1024.0:N1} MB");
        output.WriteLine($"records             : {records:N0}");
        output.WriteLine($"elapsed             : {stopwatch.Elapsed.TotalSeconds:N2} s");
        output.WriteLine($"throughput          : {records / stopwatch.Elapsed.TotalSeconds:N0} records/s");
        output.WriteLine($"throughput          : {sizeBytes / 1024.0 / 1024.0 / stopwatch.Elapsed.TotalSeconds:N1} MB/s");
        output.WriteLine($"managed heap growth : {live / 1024.0 / 1024.0:N1} MB");
        output.WriteLine($"total allocated     : {allocated / 1024.0 / 1024.0:N1} MB (cumulative, not peak)");

        Assert.Equal(29_152, records);

        // The point of streaming: live memory stays a small fraction of the file, so a
        // 122 MB exhibit and a 1 GB one cost the same.
        Assert.True(
            live < sizeBytes / 4,
            $"Managed heap grew {live:N0} bytes parsing a {sizeBytes:N0} byte file; the parser is not streaming.");
    }
}
