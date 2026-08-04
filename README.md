# opendal-dotnet-io-bench

Micro-benchmarks comparing the [Apache OpenDAL](https://github.com/apache/opendal) .NET binding against `System.IO` on the local filesystem. Written to answer a review question on the binding's FFI-boundary rework PR: *"how does the opendal binding's performance compare with dotnet's native IO?"*

Both stacks operate on the same temp directory and the same payload file, so every row is an apples-to-apples pair: the opendal `fs` service (Rust `std::fs` behind the FFI boundary) versus the closest `System.IO` equivalent.

## What it measures

| category | System.IO side | OpenDAL side |
| --- | --- | --- |
| read | `File.ReadAllBytes` | `Operator.Read` (byte[]), `Operator.Read<T>` (sequence callback) |
| read-stream | `FileStream`, 64 KiB chunks | `Operator.OpenReadStream`, 64 KiB chunks |
| read-async | `File.ReadAllBytesAsync` | `Operator.ReadAsync` |
| write | `File.WriteAllBytes` | `Operator.Write` (byte[]), `Operator.Write` (fill callback) |
| write-async | `File.WriteAllBytesAsync` | `Operator.WriteAsync` |

Payload sizes: 16 KiB, 16 MiB, 128 MiB.

## Methodology

- [BenchmarkDotNet](https://benchmarkdotnet.org/) with the in-process toolchain (the native library loads once), 2 warmup + 8 measured iterations, `MemoryDiagnoser` for managed allocations. The write categories were re-measured with `--filter "*Write*" --iterationCount 20 --warmupCount 3` because OS write-back makes Windows disk writes noisy.
- The read target file is written once during setup, so reads are served from the OS page cache **by design**: the comparison isolates API and FFI overhead, not disk speed.
- Writes go to separate files per method inside the same directory. Neither stack issues fsync.
- The System.IO method in each category is the BenchmarkDotNet baseline, so the `Ratio` column reads directly as "opendal cost relative to native".

## Running it

Expects an opendal checkout side by side with this repo:

```
parent/
  opendal/                  <- github.com/apache/opendal
  opendal-dotnet-io-bench/  <- this repo
```

1. Build the binding's native library: `cargo build --release` inside `opendal/bindings/dotnet`.
2. `dotnet run -c Release -- --filter "*"` inside this repo.

Results land in `BenchmarkDotNet.Artifacts/results/`.

## Results

Highlights from the run below:

- **The binding pays a fixed per-operation cost** — roughly 0.1 to 0.4 ms of FFI transitions plus async-runtime dispatch that `System.IO` never pays. On 16 KiB payloads that fixed cost is the whole story and the ratios run 2.6x to 13.8x in favor of `System.IO`.
- **Streaming closes the gap completely.** The chunked read pair goes from 4.2x at 16 KiB to 1.12x at 16 MiB, and at 128 MiB the opendal stream is *faster* than `FileStream` (0.91x) while allocating less than half as much per pass.
- **One-shot reads settle at 1.5x to 2x for large payloads.** The sequence-callback form is the cheaper opendal option at every size, 1.55x at 128 MiB while allocating 6 KB instead of 134 MB.
- **Writes converge as the disk takes over.** Roughly 5x at 16 MiB (where `File.WriteAllBytes` is a pure page-cache dump) narrows to about 1.4x at 128 MiB once OS write-back throttles both stacks, with the `byte[]` and fill-callback forms landing equal there.
- **Windows disk-write rows are noisy.** The write categories use 20 measured iterations against 8 for reads, and the 128 MiB rows still carry error bars around ±25% of the mean, so read those ratios as ranges. BenchmarkDotNet also runs the cases sequentially, so later write cases face more accumulated dirty pages than earlier ones. One oddity reproduced across two independent runs without being root-caused: the async 16 MiB write sits near 9x while its sync twin is 5x and its own 128 MiB neighbor is 1.4x.

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
| File_WriteAllBytesAsync | write-async | 16 KiB    |   2,106.00 μs |      87.500 μs |     97.200 μs |  1.00 |    0.06 |       604 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 16 KiB    |   4,014.00 μs |      67.900 μs |     75.500 μs |  1.91 |    0.09 |       373 B |        0.62 |
| File_WriteAllBytesAsync | write-async | 16 MiB    |   4,727.00 μs |     307.900 μs |    329.400 μs |  1.00 |    0.09 |       607 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 16 MiB    |  42,053.00 μs |   7,482.600 μs |  8,617.000 μs |  8.93 |    1.88 |       411 B |        0.68 |
| File_WriteAllBytesAsync | write-async | 128 MiB   | 217,938.00 μs |  50,104.400 μs | 57,700.300 μs |  1.07 |    0.41 |       899 B |        1.00 |
| OpenDAL_WriteAsync      | write-async | 128 MiB   | 281,108.00 μs |  43,411.900 μs | 49,993.300 μs |  1.38 |    0.45 |       693 B |        0.77 |

Gen0/Gen1/Gen2 columns omitted here for width, the full BenchmarkDotNet report lands in `BenchmarkDotNet.Artifacts/results/` when you run it yourself.

## Reading the numbers

- The gap between the two stacks is a fixed per-operation cost (FFI transitions, and for async the executor dispatch), so it is most visible at 16 KiB and fades as the payload grows and memcpy/IO dominates.
- opendal's value is the same API over object storage, file storage, and everything else it supports. `System.IO` is the floor for the one service where .NET has a native equivalent — the interesting question is how close the abstraction gets to that floor, and these tables answer it.

## Environment

AMD Ryzen 5 9600X (6C/12T), 32 GB DDR5-4800, NVMe SSD, Windows 11, .NET 8.0.29 host, BenchmarkDotNet v0.14.0, opendal binding built at the `dotnet-ffi-refactor-perf` branch in release mode.
