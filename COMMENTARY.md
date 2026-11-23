# Technical Commentary: Parallel Pixel Computation System

## Multithreading and Jobs Implementation

To compute the exact sum of Red (R) channel values from textures loaded off disk, I built a two-stage parallel map-reduce pipeline using Unity's C# Job System (`IJobParallelFor` and `IJob`) with Burst compilation. Images in `Assets/StreamingAssets/TestImages/` are read via `File.ReadAllBytes` and decoded with `Texture2D.LoadImage`. Because Unity's Windows PNG decoder yields `ARGB32` textures, `ImageDiskLoader` inspects `Texture2D.format`: it uses zero-copy `GetRawTextureData<Color32>()` for `RGBA32` textures and normalizes other formats into a `NativeArray<Color32>` so Burst jobs always read accurate channel offsets.

Naive parallel reductions using atomic increments (`Interlocked.Add`) suffer severe cache-line contention. Instead, `ParallelBurstRedSumJob` partitions pixels into adaptive, L2-cache-friendly contiguous slices (`4,370`–`65,536` pixels per chunk based on `JobsUtility.JobWorkerCount`). Each worker iterates in 4,096-pixel inner blocks, accumulating bytes into a 32-bit `uint` (`4096 × 255 = 1,044,480`, preventing overflow) before widening into a 64-bit `ulong`. This enables Burst's LLVM backend to emit wide AVX2/SSE2 SIMD instructions. Workers write once to a 64-byte cache-line-aligned `PaddedChannelSum` struct (`[StructLayout(LayoutKind.Explicit, Size = 64)]`), eliminating false sharing. A chained Burst `FinalReduceChannelSumsJob` consumes the `JobHandle` dependency to sum partial totals without main-thread stalls. The system also supports single-pass four-channel reduction (`ParallelBurstRgbaSumJob`) and concurrent multi-image batches via `JobHandle.CombineDependencies`.

## Tools, Frameworks, and APIs

Built in **Unity 6 (`6000.5.6f1`)** with `com.unity.burst` (`1.8.29`), `com.unity.collections` (`6.5.0`), and `com.unity.mathematics` (`1.4.0`). Timing used `System.Diagnostics.Stopwatch.GetTimestamp` in a Windows x64 Standalone Burst AOT build on an AMD Ryzen 7 260 (8 cores / 16 threads, 15 Job Workers, 16 MB L3 cache).

## Benchmarking and Telemetry

Across four resolutions (`512²`, `1024²`, `2048²`, `4096²`) and a `4× 2048²` concurrent batch, each test ran 5 warm-up and 25 timed iterations across five strategies: single-threaded managed C#, single-threaded Burst, parallel `IJobParallelFor` without Burst, parallel Burst (Red), and parallel Burst (RGBA). Telemetry recorded min, median, mean, max, standard deviation, throughput (MPix/s), speedup, disk load/decode time, and bit-exact verification against the single-threaded baseline, exported to `BenchmarkResults/telemetry_results.csv` and `.json`.

In the Standalone AOT build, `ParallelBurstRed` achieved **14.99×** speedup at `512²` (`0.0077 ms`), peaking at **49.46×** at `1024²` (`0.0095 ms`, `110.4 GPix/s`), **18.58×** at `2048²` (`0.1010 ms`), and **4.63×** at `4096²` (`1.6406 ms`, `10.2 GPix/s`). The concurrent `4× 2048²` batch reached **4.79×** (`1.6352 ms` vs. `7.8315 ms`).

## How Performance Results Inform Optimisation

Telemetry exposes a clear cache-to-DRAM roofline transition. Inside the 16 MB L3 cache (`1024²`), Burst SIMD (`9.51×` single-threaded) combined with 15 workers approaches `50×` speedup. At `4096²` (`64 MB`), throughput plateaus near `~41–42 GB/s` (system DRAM bandwidth): summing all four channels (`ParallelBurstRgba`, `1.5931 ms`) costs no extra time over Red-only (`1.6406 ms`). A chunk-size sweep at `4096²` confirmed `16,384`–`65,536`-pixel chunks balance scheduling overhead and load distribution (`1,048,576`-pixel chunks degraded by 38%), while Editor runs highlighted Burst safety-check costs versus Standalone AOT. Consequently, production pipelines should fuse multiple pixel analytics into one Burst pass, adopt planar Structure-of-Arrays (SoA) storage to cut single-channel DRAM traffic by 4×, or offload GPU-resident textures to Compute Shaders. Furthermore, because `4096²` PNG decoding (`158.2 ms`) outweighs Burst computation (`1.64 ms`) by nearly two orders of magnitude, real-world ingestion pipelines should pipeline asynchronous disk reads or use raw/GPU-compressed formats.
