using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// Burst-compiled single-threaded IJob for summing the Red (R) channel.
    /// Used to isolate Burst + LLVM SIMD auto-vectorization gains from multithreading gains.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    public struct SingleThreadBurstRedSumJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> Pixels;
        [WriteOnly] public NativeArray<ChannelSums> Result;

        public void Execute()
        {
            int length = Pixels.Length;
            ulong totalR = 0;
            int i = 0;

            // Process in 4096-pixel blocks using 32-bit uint accumulation for wide SIMD vectorization
            // without risking 32-bit overflow (4096 * 255 = 1,044,480 << uint.MaxValue).
            const int blockSize = 4096;
            while (i < length)
            {
                int blockEnd = math.min(i + blockSize, length);
                uint blockR = 0;
                for (; i < blockEnd; i++)
                {
                    blockR += Pixels[i].r;
                }
                totalR += blockR;
            }

            Result[0] = new ChannelSums(totalR);
        }
    }

    /// <summary>
    /// Multithreaded IJobParallelFor WITHOUT [BurstCompile].
    /// Used to isolate Unity Job System multithreading speedup from Burst native compilation.
    /// </summary>
    public struct ParallelNoBurstRedSumJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> Pixels;
        [WriteOnly] public NativeArray<PaddedChannelSum> PartialSums;
        public int ChunkSize;

        public void Execute(int chunkIndex)
        {
            int start = chunkIndex * ChunkSize;
            int end = start + ChunkSize;
            int length = Pixels.Length;
            if (end > length) end = length;

            ulong localR = 0;
            for (int i = start; i < end; i++)
            {
                localR += Pixels[i].r;
            }

            PartialSums[chunkIndex] = new PaddedChannelSum { R = localR };
        }
    }

    /// <summary>
    /// Primary high-performance Red (R) channel parallel reduction job using Unity Jobs + Burst.
    /// Each worker processes a contiguous cache-friendly slice of pixels using 32-bit inner-block
    /// accumulation (enabling LLVM AVX2/SSE2 auto-vectorization) and writes once to a 64-byte
    /// cache-line-aligned slot to eliminate false sharing and atomic contention.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    public struct ParallelBurstRedSumJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> Pixels;
        [WriteOnly] public NativeArray<PaddedChannelSum> PartialSums;
        public int ChunkSize;

        public void Execute(int chunkIndex)
        {
            int start = chunkIndex * ChunkSize;
            int end = math.min(start + ChunkSize, Pixels.Length);

            ulong localR = 0;
            int i = start;
            const int blockSize = 4096;

            while (i < end)
            {
                int blockEnd = math.min(i + blockSize, end);
                uint blockR = 0;
                for (; i < blockEnd; i++)
                {
                    blockR += Pixels[i].r;
                }
                localR += blockR;
            }

            PartialSums[chunkIndex] = new PaddedChannelSum { R = localR };
        }
    }

    /// <summary>
    /// Extended Burst parallel reduction job computing R, G, B, and A channel sums simultaneously
    /// in a single linear memory sweep (maximizing memory bandwidth utilization).
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    public struct ParallelBurstRgbaSumJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Color32> Pixels;
        [WriteOnly] public NativeArray<PaddedChannelSum> PartialSums;
        public int ChunkSize;

        public void Execute(int chunkIndex)
        {
            int start = chunkIndex * ChunkSize;
            int end = math.min(start + ChunkSize, Pixels.Length);

            ulong localR = 0;
            ulong localG = 0;
            ulong localB = 0;
            ulong localA = 0;

            int i = start;
            const int blockSize = 4096;

            while (i < end)
            {
                int blockEnd = math.min(i + blockSize, end);
                uint blockR = 0;
                uint blockG = 0;
                uint blockB = 0;
                uint blockA = 0;

                for (; i < blockEnd; i++)
                {
                    Color32 px = Pixels[i];
                    blockR += px.r;
                    blockG += px.g;
                    blockB += px.b;
                    blockA += px.a;
                }

                localR += blockR;
                localG += blockG;
                localB += blockB;
                localA += blockA;
            }

            PartialSums[chunkIndex] = new PaddedChannelSum
            {
                R = localR,
                G = localG,
                B = localB,
                A = localA
            };
        }
    }

    /// <summary>
    /// Chained Burst reduction job that combines all per-chunk partial sums into the final totals.
    /// Scheduled with the parallel map job's JobHandle as its dependency.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    public struct FinalReduceChannelSumsJob : IJob
    {
        [ReadOnly] public NativeArray<PaddedChannelSum> PartialSums;
        [WriteOnly] public NativeArray<ChannelSums> Result;

        public void Execute()
        {
            ulong r = 0, g = 0, b = 0, a = 0;
            int count = PartialSums.Length;
            for (int i = 0; i < count; i++)
            {
                PaddedChannelSum s = PartialSums[i];
                r += s.R;
                g += s.G;
                b += s.B;
                a += s.A;
            }

            Result[0] = new ChannelSums(r, g, b, a);
        }
    }
}
