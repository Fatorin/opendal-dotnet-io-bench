using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using OpenDAL;

namespace OpenDALIoBench;

/// <summary>
/// Compares the OpenDAL .NET binding (fs service) against System.IO on the
/// same directory and the same payload. Reads hit the OS page cache by
/// design, so the numbers isolate API and FFI overhead rather than disk
/// speed.
/// </summary>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class IoBenchmarks
{
    private class BenchConfig : ManualConfig
    {
        public BenchConfig()
        {
            // In-process keeps the native library loaded once instead of
            // re-resolving it in generated child projects.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(8));
        }
    }

    [Params(16 * 1024, 16 * 1024 * 1024, 128 * 1024 * 1024)]
    public int SizeBytes;

    private const string ReadKey = "payload.bin";

    private readonly byte[] chunk = new byte[64 * 1024];

    private string root = null!;
    private string readPath = null!;
    private string nativeSyncWritePath = null!;
    private string nativeAsyncWritePath = null!;
    private Operator op = null!;
    private byte[] payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), $"opendal-io-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        payload = new byte[SizeBytes];
        Random.Shared.NextBytes(payload);

        readPath = Path.Combine(root, ReadKey);
        nativeSyncWritePath = Path.Combine(root, "native-sync.bin");
        nativeAsyncWritePath = Path.Combine(root, "native-async.bin");
        File.WriteAllBytes(readPath, payload);

        op = new Operator("fs", new Dictionary<string, string> { ["root"] = root });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        op.Dispose();
        Directory.Delete(root, recursive: true);
    }

    // ---- one-shot read, payload materialized as byte[] ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("read")]
    public byte[] File_ReadAllBytes() => File.ReadAllBytes(readPath);

    [Benchmark]
    [BenchmarkCategory("read")]
    public byte[] OpenDAL_Read() => op.Read(ReadKey);

    [Benchmark]
    [BenchmarkCategory("read")]
    public long OpenDAL_ReadCallback() => op.Read(ReadKey, static sequence => sequence.Length);

    // ---- streaming read, 64 KiB chunks into a reused buffer ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("read-stream")]
    public long FileStream_Chunks()
    {
        using var stream = File.OpenRead(readPath);
        return DrainChunks(stream);
    }

    [Benchmark]
    [BenchmarkCategory("read-stream")]
    public long OpenDAL_InputStream()
    {
        using var stream = op.OpenReadStream(ReadKey);
        return DrainChunks(stream);
    }

    // ---- async one-shot read ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("read-async")]
    public Task<byte[]> File_ReadAllBytesAsync() => File.ReadAllBytesAsync(readPath);

    [Benchmark]
    [BenchmarkCategory("read-async")]
    public Task<byte[]> OpenDAL_ReadAsync() => op.ReadAsync(ReadKey);

    // ---- one-shot write ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("write")]
    public void File_WriteAllBytes() => File.WriteAllBytes(nativeSyncWritePath, payload);

    [Benchmark]
    [BenchmarkCategory("write")]
    public void OpenDAL_Write() => op.Write("opendal-sync.bin", payload);

    [Benchmark]
    [BenchmarkCategory("write")]
    public void OpenDAL_WriteCallback() =>
        op.Write(
            "opendal-fill.bin",
            writer =>
            {
                payload.CopyTo(writer.GetSpan(payload.Length));
                writer.Advance(payload.Length);
            },
            sizeHint: payload.Length);

    // ---- async one-shot write ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("write-async")]
    public Task File_WriteAllBytesAsync() => File.WriteAllBytesAsync(nativeAsyncWritePath, payload);

    [Benchmark]
    [BenchmarkCategory("write-async")]
    public Task OpenDAL_WriteAsync() => op.WriteAsync("opendal-async.bin", payload);

    private long DrainChunks(Stream stream)
    {
        long total = 0;
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += n;
        }

        return total;
    }
}
