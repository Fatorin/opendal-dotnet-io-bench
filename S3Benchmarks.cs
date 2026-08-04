using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using OpenDAL;

namespace OpenDALIoBench;

/// <summary>
/// Compares the OpenDAL .NET binding (s3 service) against AWSSDK.S3, both
/// talking to the same local MinIO over loopback with their default
/// configuration. Run MinIO first, see the README.
/// </summary>
[Config(typeof(BenchConfig))]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class S3Benchmarks
{
    private class BenchConfig : ManualConfig
    {
        public BenchConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(10));
        }
    }

    private const string Endpoint = "http://127.0.0.1:9000";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Bucket = "bench";

    [Params(16 * 1024, 16 * 1024 * 1024, 128 * 1024 * 1024)]
    public int SizeBytes;

    private AmazonS3Client s3 = null!;
    private Operator op = null!;
    private byte[] payload = null!;
    private string readKey = null!;

    [GlobalSetup]
    public void Setup()
    {
        s3 = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
            });

        try
        {
            s3.PutBucketAsync(Bucket).GetAwaiter().GetResult();
        }
        catch (AmazonS3Exception e)
            when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        op = new Operator("s3", new Dictionary<string, string>
        {
            ["bucket"] = Bucket,
            ["endpoint"] = Endpoint,
            ["access_key_id"] = AccessKey,
            ["secret_access_key"] = SecretKey,
            ["region"] = "us-east-1",
        });

        payload = new byte[SizeBytes];
        Random.Shared.NextBytes(payload);
        readKey = $"bench-read-{SizeBytes}.bin";
        op.Write(readKey, payload);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        op.Dispose();
        s3.Dispose();
    }

    // ---- read, payload materialized as byte[] ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("s3-read")]
    public async Task<byte[]> AwsS3_GetObject()
    {
        using var response = await s3.GetObjectAsync(Bucket, readKey);
        var buffer = new byte[response.ContentLength];
        await using var stream = response.ResponseStream;
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    [Benchmark]
    [BenchmarkCategory("s3-read")]
    public Task<byte[]> OpenDAL_ReadAsync() => op.ReadAsync(readKey);

    [Benchmark]
    [BenchmarkCategory("s3-read")]
    public Task<long> OpenDAL_ReadCallback() =>
        op.ReadAsync(readKey, static sequence => sequence.Length);

    // ---- write ----

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("s3-write")]
    public Task AwsS3_PutObject() =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "bench-write-aws.bin",
            InputStream = new MemoryStream(payload),
        });

    [Benchmark]
    [BenchmarkCategory("s3-write")]
    public Task OpenDAL_WriteAsync() => op.WriteAsync("bench-write-opendal.bin", payload);
}
