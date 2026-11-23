# Parallel Pixel Computation System (Unity Jobs + Burst)

A high-performance parallel image analysis system built in **Unity 6 (`6000.5.6f1`)** using the **Unity C# Job System** (`IJobParallelFor`, `IJob`, `JobHandle`) and **Burst Compiler (`1.8.29`)** to load PNG/JPG textures from disk, compute exact Red (R) and RGBA channel sums concurrently across CPU cores with LLVM SIMD auto-vectorization, log verified execution telemetry, and export structured CSV/JSON benchmarks.

> Full 500-word technical analysis: **[COMMENTARY.md](COMMENTARY.md)**

---

## Live Demo & Media Evidence

### Animated Computation & Telemetry Walkthrough
![Parallel Pixel Computation Demo](Media/demo_computation.gif)

### Benchmark Scaling Charts (Standalone Burst AOT)
![Benchmark Scaling Charts](Media/benchmark_charts.png)

### Dashboard & Log Output Screenshots

| Peak L3-Resident Speedup (`1024×1024` — **49.46×**) | 4K Memory-Bandwidth Saturation (`4096×4096` — **4.63× / 4.77×**) |
| :---: | :---: |
| ![1024x1024 Dashboard](Media/screenshot_dashboard_1024.png) | ![4096x4096 Dashboard](Media/screenshot_dashboard_4096.png) |

| Concurrent 4-Image Batch (`4× 2048×2048` — **4.79×**) | Full Console & Chunk-Size Sweep Log |
| :---: | :---: |
| ![Multi-Image Batch Dashboard](Media/screenshot_batch_4x2048.png) | ![Console Log Output](Media/screenshot_console_log.png) |

---

## Key Features Implemented

### Core Requirements
- **Disk Image Loading (`ImageDiskLoader.cs`)**: Reads raw PNG/JPG files from disk (`Assets/StreamingAssets/TestImages/`) via `File.ReadAllBytes` + `Texture2D.LoadImage`, explicitly handles `RGBA32` zero-copy `GetRawTextureData<Color32>()` vs. `ARGB32`/`RGB24` normalization, and logs disk I/O and decode times separately.
- **Unity Jobs + Burst Parallel Red-Channel Reduction (`RedChannelSumJobs.cs`)**:
  - Two-stage map-reduce pipeline (`ParallelBurstRedSumJob` -> chained `FinalReduceChannelSumsJob` via `JobHandle`).
  - Adaptive L2-cache-friendly chunking (`4,370`–`65,536` px per chunk) with 4,096-pixel `uint` inner-block accumulation (enabling AVX2/SSE2 SIMD vectorization without 32-bit overflow).
  - 64-byte cache-line-aligned partial sum slots (`PaddedChannelSum`) to eliminate false sharing and atomic contention.
- **High-Resolution Execution Timing & Verification (`PixelSumBenchmarkRunner.cs`)**: Sub-microsecond timing via `Stopwatch.GetTimestamp()` (5 warm-up + 25 timed runs) with bit-exact verification against the single-threaded C# baseline.

### Optional Extensions Included
- **Single-Thread vs. Multithreaded vs. Burst Isolation**: Compares `SingleThreadManaged`, `SingleThreadBurst`, `ParallelNoBurst`, `ParallelBurstRed`, and `ParallelBurstRgba`.
- **Live In-App Visualisation + External Charts**: Interactive runtime dashboard (`ParallelImageAnalyzerDashboard.cs`) plus multi-panel performance charts (`Media/benchmark_charts.png`).
- **CSV & JSON Telemetry Export (`TelemetryExporter.cs`)**: Writes complete hardware metadata and statistics to [`BenchmarkResults/telemetry_results.csv`](BenchmarkResults/telemetry_results.csv) and [`BenchmarkResults/telemetry_results.json`](BenchmarkResults/telemetry_results.json).
- **Concurrent Multi-Channel & Multi-Image Processing**: Single-pass 4-channel RGBA reduction (`ParallelBurstRgbaSumJob`) and concurrent 4-image batch scheduling via `JobHandle.CombineDependencies`.

---

## Standalone Burst AOT Benchmark Summary

*Hardware: AMD Ryzen 7 260 (8 cores / 16 logical threads, 15 Unity Job Workers, 16 MB L3 cache) | Unity `6000.5.6f1` | Windows x64 Standalone (Burst AOT) | Median of 25 runs (after 5 warm-up runs)*

| Image | Pixels | Single-Thread C# | Single-Thread Burst | Parallel (No Burst) | **Parallel + Burst (Red)** | **Parallel + Burst (RGBA)** | Peak Speedup | Exact Red Sum |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `test_512x512.png` | 262,144 | 0.1154 ms | 0.0163 ms | 0.0441 ms | **0.0077 ms** | 0.0082 ms | **14.99×** | `33,391,472` |
| `test_1024x1024.png` | 1,048,576 | 0.4699 ms | 0.0494 ms | 0.1208 ms | **0.0095 ms** | 0.0122 ms | **49.46×** | `128,991,620` |
| `test_2048x2048.png` | 4,194,304 | 1.8766 ms | 0.3859 ms | 0.3568 ms | **0.1010 ms** | 0.1084 ms | **18.58×** | `534,285,156` |
| `test_4096x4096.png` | 16,777,216 | 7.5926 ms | 2.0039 ms | 1.7190 ms | **1.6406 ms** | **1.5931 ms** | **4.77×** | `2,063,867,120` |
| `Batch 4× 2048×2048` | 16,777,216 | 7.8315 ms (seq) | — | — | **1.6352 ms** (concurrent) | — | **4.79×** | `2,068,946,242` |

---

## Project Structure

```text
├── Assets/
│   ├── Editor/
│   │   └── ParallelAnalysisEditorTools.cs      # Scene setup, Editor benchmark runner & Windows build menu
│   ├── Scenes/
│   │   └── SampleScene.unity                   # Pre-configured runnable scene with dashboard controller
│   ├── Scripts/ParallelPixelAnalysis/
│   │   ├── PixelAnalysisModels.cs              # PaddedChannelSum (64B aligned), ChannelSums, Telemetry models
│   │   ├── ImageDiskLoader.cs                  # Disk PNG/JPG loader & RGBA32 format normalizer
│   │   ├── RedChannelSumJobs.cs                # Burst & non-Burst IJob / IJobParallelFor reduction jobs
│   │   ├── PixelSumBenchmarkRunner.cs          # Benchmarking, adaptive chunking & bit-exact verification
│   │   ├── TelemetryExporter.cs                # CSV/JSON export & formatted console logging
│   │   └── ParallelImageAnalyzerDashboard.cs   # Live runtime dashboard UI & automated demo capture
│   └── StreamingAssets/TestImages/             # Disk PNG test suite (512²–4096² + 4× 2048² batch)
├── BenchmarkResults/
│   ├── telemetry_results.csv                   # Standalone Burst AOT telemetry (CSV)
│   ├── telemetry_results.json                  # Standalone Burst AOT telemetry (JSON)
│   ├── editor_telemetry_results.csv            # Unity Editor comparison telemetry (CSV)
│   └── editor_telemetry_results.json           # Unity Editor comparison telemetry (JSON)
├── Media/
│   ├── demo_computation.gif                    # Animated walkthrough of computation & logs
│   ├── benchmark_charts.png                    # Multi-panel latency, speedup & roofline charts
│   ├── screenshot_dashboard_1024.png           # Dashboard screenshot (1024×1024 peak speedup)
│   ├── screenshot_dashboard_4096.png           # Dashboard screenshot (4096×4096 DRAM-bound)
│   ├── screenshot_batch_4x2048.png             # Dashboard screenshot (4× concurrent multi-image batch)
│   └── screenshot_console_log.png              # Console & chunk-size tuning sweep output
├── COMMENTARY.md                               # 500-word technical commentary
└── README.md
```

---

## How to Run

1. Open the project in **Unity `6000.5.6f1`** (or newer Unity 6).
2. Open `Assets/Scenes/SampleScene.unity` and press **Play** — the dashboard immediately loads `test_2048x2048.png` from disk, runs all five strategies, verifies bit-exact totals, and displays live charts and logs.
3. Use the toolbar buttons to switch resolutions (`512x512`–`4096x4096`), run the full scaling suite, execute the `4× 2048×2048` concurrent batch, or export fresh CSV/JSON telemetry.
4. For true Burst AOT performance without Editor safety-check overhead, use **`Tools -> Parallel Pixel Analysis -> 3. Build Windows Standalone Player`** and launch `Builds/Windows/ParallelPixelAnalysis.exe`.
