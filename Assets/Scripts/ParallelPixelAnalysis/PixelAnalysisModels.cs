using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// 64-byte cache-line-aligned accumulator per parallel chunk to prevent false sharing
    /// across CPU cores during concurrent writes from Unity Job Worker threads.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct PaddedChannelSum
    {
        [FieldOffset(0)] public ulong R;
        [FieldOffset(8)] public ulong G;
        [FieldOffset(16)] public ulong B;
        [FieldOffset(24)] public ulong A;
    }

    /// <summary>
    /// Immutable channel sum totals for an image.
    /// </summary>
    [Serializable]
    public struct ChannelSums
    {
        public ulong r;
        public ulong g;
        public ulong b;
        public ulong a;

        public ChannelSums(ulong r, ulong g = 0, ulong b = 0, ulong a = 0)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    /// <summary>
    /// Supported execution strategies for pixel analysis benchmarking.
    /// </summary>
    public enum AnalysisStrategy
    {
        SingleThreadManaged,
        SingleThreadBurst,
        ParallelNoBurst,
        ParallelBurstRed,
        ParallelBurstRgba,
        MultiImageSequentialManaged,
        MultiImageConcurrentBurst
    }

    /// <summary>
    /// Telemetry record for a single benchmarked image/strategy run.
    /// </summary>
    [Serializable]
    public class BenchmarkEntry
    {
        public string timestampUtc;
        public string imageName;
        public int width;
        public int height;
        public long pixelCount;
        public string textureFormat;
        public double diskLoadMs;

        public string strategy;
        public int chunkSize;
        public int innerLoopBatchCount;
        public int warmupIterations;
        public int measuredIterations;

        public double minMs;
        public double medianMs;
        public double meanMs;
        public double maxMs;
        public double stdDevMs;
        public double throughputMPixPerSec;
        public double speedupVsManaged;

        public ulong redSum;
        public ulong greenSum;
        public ulong blueSum;
        public ulong alphaSum;
        public bool matchesReference;
    }

    /// <summary>
    /// Top-level JSON export payload containing hardware metadata and all benchmark entries.
    /// </summary>
    [Serializable]
    public class BenchmarkReport
    {
        public string generatedAtUtc;
        public string unityVersion;
        public string runtimeEnvironment;
        public string processorType;
        public int logicalProcessorCount;
        public int jobWorkerCount;
        public bool burstEnabled;
        public int systemMemoryMB;
        public List<BenchmarkEntry> entries = new List<BenchmarkEntry>();
    }
}
