# Technical Commentary: Parallel Pixel Computation System

When the program runs, `ParallelImageAnalyzerDashboard.cs` finds the PNG assets in `Assets/StreamingAssets/TestImages/` and triggers the pipeline to compute their Red-channel sums. `ImageDiskLoader.cs` decodes each PNG into a `NativeArray`, ensuring the data is stored as [R, G, B, A] so it can be processed correctly. This is passed to `PixelSumBenchmarkRunner.cs`, which calculates a chunk size that fits in the CPU L2 cache to reduce latency, then tests the same image across 5 methods, changing one variable at a time. First is a plain C# single-threaded baseline, followed by single-threaded Burst + SIMD compiling C# into native machine code. Method 3 splits chunks across CPU threads with `IJobParallelFor`, while methods 4 and 5 combine `IJobParallelFor`, Burst, and a chained `IJob` to merge results, with method 5 summing all four RGBA channels in one pass. Each method runs 25 iterations timed with `Stopwatch.GetTimestamp`, taking the median to reduce anomalies.

The system was built in Unity 6.000.5.6f1 using `com.unity.burst`, `com.unity.collections`, and `com.unity.mathematics`, and benchmarked in a Windows standalone Burst ahead-of-time build on an AMD Ryzen 7 260 (15 workers, 16MB L3 cache) without editor overheads or safety checks for accurate results. `TelemetryExporter.cs` exports results to CSV, logging image size, method, hardware info, min/max/median/mean times, and throughput.

The parallel Burst Red method peaked at a 49.46x speedup on 1024x1024 (0.0095ms), dropping to 4.63x at 4096x4096 (1.64ms) and 4.79x on the 4x 2048x2048 batch, whereas running in the Unity Editor limited peak speedup to 3.59x due to Burst safety checks. This drop happens because 1024x1024 (4MB) fits inside the L3 cache (110.4 GPix/s), while 4096x4096 (64MB) hits slower system RAM bandwidth (~41-42 GB/s), shown by RGBA (1.59ms) taking no longer than Red alone. For future optimisation, combining calculations into one pass or storing colour channels separately would reduce RAM traffic, while async loading, raw formats, or Compute Shaders would address 4096x4096 PNG decoding (158.18ms), which is 96x slower than Burst.

## Benchmark Graphs

![Execution Time and Speedup Scaling](Media/Graphs/01_execution_time_and_speedup.png)

![Cache-to-DRAM Roofline Transition](Media/Graphs/02_cache_to_dram_roofline.png)

![Standalone Burst AOT vs Unity Editor](Media/Graphs/03_standalone_aot_vs_editor.png)

![4K PNG Disk Decode vs Burst Compute](Media/Graphs/04_disk_decode_vs_compute.png)
