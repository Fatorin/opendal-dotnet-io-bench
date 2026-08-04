# opendal-dotnet-io-bench

Micro-benchmarks comparing the [Apache OpenDAL](https://github.com/apache/opendal) .NET binding against `System.IO` on the local filesystem. Written to answer a review question on the binding's FFI-boundary rework PR: *"how does the opendal binding's performance compare with dotnet's native IO?"*

Both stacks operate on the same temp directory and the same payload file, so every row is an apples-to-apples pair: the opendal `fs` service (Rust `std::fs` behind the FFI boundary) versus the closest `System.IO` equivalent. A second suite runs the same idea over object storage: the opendal `s3` service versus `AWSSDK.S3`, both talking to the same local MinIO over loopback.

## What it measures

| category | System.IO side | OpenDAL side |
| --- | --- | --- |
| read | `File.ReadAllBytes` | `Operator.Read` (byte[]), `Operator.Read<T>` (sequence callback) |
| read-stream | `FileStream`, 64 KiB chunks | `Operator.OpenReadStream`, 64 KiB chunks |
| read-async | `File.ReadAllBytesAsync` | `Operator.ReadAsync` |
| write | `File.WriteAllBytes` | `Operator.Write` (byte[]), `Operator.Write` (fill callback) |
| write-async | `File.WriteAllBytesAsync` | `Operator.WriteAsync` |
| s3-read | `AWSSDK.S3` `GetObjectAsync` into a byte[] | `Operator.ReadAsync` (byte[]), `Operator.ReadAsync<T>` (sequence callback) |
| s3-write | `AWSSDK.S3` `PutObjectAsync` | `Operator.WriteAsync` |

Payload sizes: 16 KiB, 16 MiB, 128 MiB.

## Methodology

- [BenchmarkDotNet](https://benchmarkdotnet.org/) with the in-process toolchain (the native library loads once), 2 warmup + 8 measured iterations, `MemoryDiagnoser` for managed allocations. The write categories were re-measured with `--filter "*Write*" --iterationCount 20 --warmupCount 3` because OS write-back makes Windows disk writes noisy, and the write-async category was additionally run in isolation (`--filter "*Write*Async*"`) so the preceding 128 MiB sync-write cases' flushing stays out of its rows.
- The read target file is written once during setup, so reads are served from the OS page cache **by design**: the comparison isolates API and FFI overhead, not disk speed.
- Writes go to separate files per method inside the same directory. Neither stack issues fsync.
- The System.IO method in each category is the BenchmarkDotNet baseline, so the `Ratio` column reads directly as "opendal cost relative to native". In the s3 suite the `AWSSDK.S3` method plays that role.
- The s3 suite runs both clients at their default configuration against the same MinIO instance over plain HTTP loopback, with 2 warmup + 10 measured iterations.

## Running it

Expects an opendal checkout side by side with this repo:

```
parent/
  opendal/                  <- github.com/apache/opendal
  opendal-dotnet-io-bench/  <- this repo
```

1. Build the binding's native library: `cargo build --release` inside `opendal/bindings/dotnet`.
2. `dotnet run -c Release -- --filter "*IoBenchmarks*"` for the `System.IO` suite.
3. For the s3 suite, start MinIO first, then `dotnet run -c Release -- --filter "*S3Benchmarks*"`. The bucket is created automatically:

```
docker run -d --name minio -p 9000:9000 -e MINIO_ROOT_USER=minioadmin -e MINIO_ROOT_PASSWORD=minioadmin quay.io/minio/minio server /data
```

Results land in `BenchmarkDotNet.Artifacts/results/`.

## Results

Highlights from the run below:

- **The binding pays a fixed per-operation cost** — roughly 0.1 to 0.4 ms of FFI transitions plus async-runtime dispatch that `System.IO` never pays. On 16 KiB payloads that fixed cost is the whole story and the ratios run 2.6x to 13.8x in favor of `System.IO`.
- **Streaming closes the gap completely.** The chunked read pair goes from 4.2x at 16 KiB to 1.12x at 16 MiB, and at 128 MiB the opendal stream is *faster* than `FileStream` (0.91x) while allocating less than half as much per pass.
- **One-shot reads settle at 1.5x to 2x for large payloads.** The sequence-callback form is the cheaper opendal option at every size, 1.55x at 128 MiB while allocating 6 KB instead of 134 MB.
- **Writes converge as the disk takes over.** Roughly 5x at 16 MiB (where `File.WriteAllBytes` is a pure page-cache dump) narrows to roughly 1.4-1.7x at 128 MiB once OS write-back throttles both stacks, with the `byte[]` and fill-callback forms landing equal there.
- **Windows disk-write rows are noisy.** The write categories use 20 measured iterations against 8 for reads, and the 128 MiB rows still carry error bars around ±25% of the mean, so read those ratios as ranges. BenchmarkDotNet also runs the cases sequentially, so a case that follows the 128 MiB writes inherits their write-back flushing: full-suite runs put the async 16 MiB write near 9x for exactly that reason, while a paired sync/async/`Task.Run` trace shows the three paths within 1% of each other at every size from 1 to 128 MiB. The write-async rows above therefore come from an isolated run of that category, where async lands next to its sync twin (5.4x vs 5.0x at 16 MiB). The trace ships in this repo — reproduce it with `dotnet run -c Release -- write-trace`.

| Method                  | Categories  | SizeBytes | Mean          | Error          | StdDev        | Ratio | RatioSD | Allocated   | Alloc Ratio |
|------------------------ |------------ |---------- |--------------:|---------------:|--------------:|------:|--------:|------------:|------------:|
| File_ReadAllBytes       | read        | 16 KiB    |      28.00 μs |       0.576 μs |      0.256 μs |  1.00 |    0.01 |     16480 B |       1.000 |
| OpenDAL_Read            | read        | 16 KiB    |     385.40 μs |      43.617 μs |     22.812 μs | 13.77 |    0.78 |     16568 B |       1.005 |
| OpenDAL_ReadCallback    | read        | 16 KiB    |     360.64 μs |      25.304 μs |     13.235 μs | 12.88 |    0.46 |       160 B |       0.010 |
| File_ReadAllBytes       | read        | 16 MiB    |   2,587.76 μs |      70.091 μs |     31.121 μs |  1.00 |    0.02 |  16778134 B |       1.000 |
| OpenDAL_Read            | read        | 16 MiB    |   6,484.21 μs |     394.411 μs |    175.121 μs |  2.51 |    0.07 |  16778730 B |       1.000 |
| OpenDAL_ReadCallback    | read        | 16 MiB    |   4,268.72 μs |     426.065 μs |    222.840 μs |  1.65 |    0.08 |       883 B |       0.000 |
| File_ReadAllBytes       | read        | 128 MiB   |  23,886.87 μs |   2,581.194 μs |  1,146.066 μs |  1.00 |    0.06 | 134218574 B |       1.000 |
| OpenDAL_Read            | read        | 128 MiB   |  47,899.51 μs |     571.759 μs |    299.041 μs |  2.01 |    0.09 | 134224468 B |       1.000 |
| OpenDAL_ReadCallback    | read        | 128 MiB   |  36,849.61 μs |     502.816 μs |    262.982 μs |  1.55 |    0.07 |      6305 B |       0.000 |
| File_ReadAllBytesAsync  | read-async  | 16 KiB    |     110.18 μs |       3.136 μs |      1.640 μs |  1.00 |    0.02 |     17015 B |        1.00 |
| OpenDAL_ReadAsync       | read-async  | 16 KiB    |     391.66 μs |       9.188 μs |      4.080 μs |  3.56 |    0.06 |     17144 B |        1.01 |
| File_ReadAllBytesAsync  | read-async  | 16 MiB    |   2,863.68 μs |     114.946 μs |     51.037 μs |  1.00 |    0.02 |  16778670 B |        1.00 |
| OpenDAL_ReadAsync       | read-async  | 16 MiB    |   7,533.03 μs |   1,874.842 μs |    980.579 μs |  2.63 |    0.33 |  16779306 B |        1.00 |
| File_ReadAllBytesAsync  | read-async  | 128 MiB   |  29,290.37 μs |   5,879.299 μs |  3,074.987 μs |  1.01 |    0.14 | 134219110 B |        1.00 |
| OpenDAL_ReadAsync       | read-async  | 128 MiB   |  52,859.77 μs |   3,766.085 μs |  1,672.165 μs |  1.82 |    0.18 | 134225027 B |        1.00 |
| FileStream_Chunks       | read-stream | 16 KiB    |      29.87 μs |       1.316 μs |      0.688 μs |  1.00 |    0.03 |       240 B |        1.00 |
| OpenDAL_InputStream     | read-stream | 16 KiB    |     125.00 μs |       7.697 μs |      4.025 μs |  4.19 |    0.16 |        88 B |        0.37 |
| FileStream_Chunks       | read-stream | 16 MiB    |   1,549.94 μs |     104.602 μs |     54.709 μs |  1.00 |    0.05 |       242 B |        1.00 |
| OpenDAL_InputStream     | read-stream | 16 MiB    |   1,729.61 μs |      58.685 μs |     30.693 μs |  1.12 |    0.04 |        89 B |        0.37 |
| FileStream_Chunks       | read-stream | 128 MiB   |  19,131.23 μs |   1,839.580 μs |    962.136 μs |  1.00 |    0.07 |       268 B |        1.00 |
| OpenDAL_InputStream     | read-stream | 128 MiB   |  17,291.28 μs |   1,778.185 μs |    789.525 μs |  0.91 |    0.06 |       110 B |        0.41 |
| File_WriteAllBytes      | write       | 16 KiB    |   1,540.00 μs |      41.000 μs |     45.600 μs |  1.00 |    0.04 |        74 B |        1.00 |
| OpenDAL_Write           | write       | 16 KiB    |   4,029.00 μs |      21.200 μs |     22.700 μs |  2.62 |    0.08 |         5 B |        0.07 |
| OpenDAL_WriteCallback   | write       | 16 KiB    |   4,283.00 μs |     241.600 μs |    278.200 μs |  2.78 |    0.19 |       133 B |        1.80 |
| File_WriteAllBytes      | write       | 16 MiB    |   4,747.00 μs |     214.300 μs |    238.200 μs |  1.00 |    0.07 |        79 B |        1.00 |
| OpenDAL_Write           | write       | 16 MiB    |  23,859.00 μs |   1,467.500 μs |  1,507.000 μs |  5.04 |    0.39 |        22 B |        0.28 |
| OpenDAL_WriteCallback   | write       | 16 MiB    |  31,429.00 μs |     384.200 μs |    411.100 μs |  6.64 |    0.32 |       171 B |        2.16 |
| File_WriteAllBytes      | write       | 128 MiB   | 202,122.00 μs |  46,356.900 μs | 53,384.700 μs |  1.06 |    0.37 |       371 B |        1.00 |
| OpenDAL_Write           | write       | 128 MiB   | 269,984.00 μs |  47,886.200 μs | 55,145.800 μs |  1.42 |    0.44 |       344 B |        0.93 |
| OpenDAL_WriteCallback   | write       | 128 MiB   | 274,520.00 μs |  46,524.100 μs | 53,577.200 μs |  1.44 |    0.44 |       472 B |        1.27 |
| File_WriteAllBytesAsync | write-async | 16 KiB    |   2,013.00 μs |      66.400 μs |     68.200 μs |  1.00 |    0.05 |       604 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 16 KiB    |   3,949.00 μs |      85.000 μs |     87.300 μs |  1.96 |    0.08 |       373 B |        0.62 |
| File_WriteAllBytesAsync | write-async | 16 MiB    |   4,647.00 μs |     341.400 μs |    379.500 μs |  1.01 |    0.11 |       607 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 16 MiB    |  24,963.00 μs |   1,445.200 μs |  1,546.300 μs |  5.40 |    0.53 |       390 B |        0.64 |
| File_WriteAllBytesAsync | write-async | 128 MiB   | 160,591.00 μs |   5,437.000 μs |  5,583.400 μs |  1.00 |    0.05 |       995 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 128 MiB   | 269,592.00 μs |  39,503.400 μs | 45,492.200 μs |  1.68 |    0.28 |       597 B |        0.60 |

Gen0/Gen1/Gen2 columns omitted here for width, the full BenchmarkDotNet report lands in `BenchmarkDotNet.Artifacts/results/` when you run it yourself.

### s3 against AWSSDK.S3 (MinIO)

Same rig, both clients at their defaults, loopback MinIO. Here the comparison flips: **opendal is faster than the AWS SDK at every size in both directions**.

- Reads land at 0.44-0.52x of the SDK's time from 16 MiB up (about 2x faster), writes at 0.63-0.65x (about 1.5x faster), and even the 16 KiB rows come in ahead.
- The allocation gap is bigger than the throughput gap. The SDK allocates about 2.4x the payload per read (internal buffering) and megabytes per large write, while the opendal `byte[]` read allocates exactly the payload and its writes stay under 2 KB. The sequence-callback read stays in the kilobytes at every size. Part of the gap likely comes from the SDK's payload signing and buffering at default settings.
- The fixed per-operation cost from the fs suite is invisible here — HTTP round trips dominate, and object storage is the binding's actual target scenario.

| Method               | Categories | SizeBytes | Mean         | Error         | StdDev        | Ratio | RatioSD | Allocated   | Alloc Ratio |
|--------------------- |----------- |---------- |-------------:|--------------:|--------------:|------:|--------:|------------:|------------:|
| AwsS3_GetObject      | s3-read    | 16 KiB    |     847.0 μs |     137.70 μs |      81.95 μs |  1.01 |    0.12 |    107616 B |       1.000 |
| OpenDAL_ReadAsync    | s3-read    | 16 KiB    |     691.7 μs |      24.19 μs |      16.00 μs |  0.82 |    0.07 |     17290 B |       0.161 |
| OpenDAL_ReadCallback | s3-read    | 16 KiB    |     686.3 μs |       7.70 μs |       5.09 μs |  0.82 |    0.07 |       884 B |       0.008 |
| AwsS3_GetObject      | s3-read    | 16 MiB    |  41,144.2 μs |   4,342.02 μs |   2,871.98 μs |  1.00 |    0.09 |  39922256 B |       1.000 |
| OpenDAL_ReadAsync    | s3-read    | 16 MiB    |  20,530.0 μs |   1,423.74 μs |     744.64 μs |  0.50 |    0.04 |  16803497 B |       0.421 |
| OpenDAL_ReadCallback | s3-read    | 16 MiB    |  17,862.7 μs |   1,340.87 μs |     886.90 μs |  0.44 |    0.03 |     24550 B |       0.001 |
| AwsS3_GetObject      | s3-read    | 128 MiB   | 282,113.8 μs |   2,263.35 μs |   1,183.78 μs |  1.00 |    0.01 | 318868456 B |       1.000 |
| OpenDAL_ReadAsync    | s3-read    | 128 MiB   | 147,726.1 μs |   5,600.69 μs |   2,929.27 μs |  0.52 |    0.01 | 134409229 B |       0.422 |
| OpenDAL_ReadCallback | s3-read    | 128 MiB   | 136,235.5 μs |   7,979.66 μs |   5,278.05 μs |  0.48 |    0.02 |    192530 B |       0.001 |
| AwsS3_PutObject      | s3-write   | 16 KiB    |   5,490.5 μs |     428.37 μs |     283.34 μs |  1.00 |    0.07 |    277150 B |       1.000 |
| OpenDAL_WriteAsync   | s3-write   | 16 KiB    |   4,792.9 μs |     510.01 μs |     337.34 μs |  0.88 |    0.07 |       379 B |       0.001 |
| AwsS3_PutObject      | s3-write   | 16 MiB    |  89,082.5 μs |   7,094.81 μs |   3,710.72 μs |  1.00 |    0.06 |   2089707 B |       1.000 |
| OpenDAL_WriteAsync   | s3-write   | 16 MiB    |  57,857.9 μs |   4,714.87 μs |   2,805.75 μs |  0.65 |    0.04 |       519 B |       0.000 |
| AwsS3_PutObject      | s3-write   | 128 MiB   | 803,726.2 μs | 152,237.06 μs | 100,695.39 μs |  1.01 |    0.16 |  14445016 B |       1.000 |
| OpenDAL_WriteAsync   | s3-write   | 128 MiB   | 496,327.9 μs |  47,016.27 μs |  24,590.42 μs |  0.63 |    0.07 |      1728 B |       0.000 |

## Reading the numbers

- The gap between the two stacks is a fixed per-operation cost (FFI transitions, and for async the executor dispatch), so it is most visible at 16 KiB and fades as the payload grows and memcpy/IO dominates.
- opendal's value is the same API over object storage, file storage, and everything else it supports. `System.IO` is the floor for the one service where .NET has a native equivalent — the interesting question is how close the abstraction gets to that floor, and these tables answer it.

## Environment

AMD Ryzen 5 9600X (6C/12T), 32 GB DDR5-4800, NVMe SSD, Windows 11, .NET 8.0.29 host, BenchmarkDotNet v0.14.0, opendal binding built at the `dotnet-ffi-refactor-perf` branch in release mode. The s3 suite runs against a single-node MinIO container with AWSSDK.S3 3.7.511.
