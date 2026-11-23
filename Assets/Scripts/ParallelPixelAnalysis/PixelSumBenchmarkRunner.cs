using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// Executes and benchmarks single-threaded, multithreaded, Burst-compiled, multi-channel,
    /// and multi-image concurrent pixel channel reduction strategies.
    /// </summary>
    public static class PixelSumBenchmarkRunner
    {
        public const int DefaultWarmupIterations = 5;
        public const int DefaultMeasuredIterations = 25;
        public const int DefaultInnerLoopBatchCount = 1;

        /// <summary>
        /// Computes an L2-cache-friendly chunk size that generates enough chunks to saturate
        /// Unity's Job Worker threads across both small (512x512) and large (4096x4096) textures.
        /// </summary>
        public static int ComputeAdaptiveChunkSize(int pixelCount)
        {
            int workerCount = math.max(JobsUtility.JobWorkerCount, 1);
            int targetChunks = math.max(workerCount * 4, 16);
            int chunkSize = (pixelCount + targetChunks - 1) / targetChunks;
            return math.clamp(chunkSize, 4096, 65536);
        }

        /// <summary>
        /// Single-threaded managed C# baseline summing the Red (R) channel.
        /// </summary>
        public static ChannelSums ComputeSingleThreadManagedRed(Color32[] pixels)
        {
            ulong sumR = 0;
            int len = pixels.Length;
            for (int i = 0; i < len; i++)
            {
                sumR += pixels[i].r;
            }
            return new ChannelSums(sumR);
        }

        /// <summary>
        /// Reference single-threaded managed C# full RGBA sum used to verify bit-exactness of all parallel jobs.
        /// </summary>
        public static ChannelSums ComputeReferenceRgba(Color32[] pixels)
        {
            ulong r = 0, g = 0, b = 0, a = 0;
            int len = pixels.Length;
            for (int i = 0; i < len; i++)
            {
                Color32 px = pixels[i];
                r += px.r;
                g += px.g;
                b += px.b;
                a += px.a;
            }
            return new ChannelSums(r, g, b, a);
        }

        /// <summary>
        /// Single-threaded Burst IJob summing the Red (R) channel.
        /// </summary>
        public static ChannelSums ComputeSingleThreadBurstRed(NativeArray<Color32> pixels)
        {
            using var result = new NativeArray<ChannelSums>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var job = new SingleThreadBurstRedSumJob
            {
                Pixels = pixels,
                Result = result
            };
            job.Schedule().Complete();
            return result[0];
        }

        /// <summary>
        /// Multithreaded IJobParallelFor WITHOUT Burst summing the Red (R) channel.
        /// </summary>
        public static ChannelSums ComputeParallelNoBurstRed(NativeArray<Color32> pixels, int chunkSize, int batchCount = DefaultInnerLoopBatchCount)
        {
            int chunkCount = (pixels.Length + chunkSize - 1) / chunkSize;
            using var partials = new NativeArray<PaddedChannelSum>(chunkCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var job = new ParallelNoBurstRedSumJob
            {
                Pixels = pixels,
                PartialSums = partials,
                ChunkSize = chunkSize
            };

            job.Schedule(chunkCount, batchCount).Complete();

            ulong totalR = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                totalR += partials[i].R;
            }
            return new ChannelSums(totalR);
        }

        /// <summary>
        /// Primary Unity Jobs + Burst parallel Red (R) channel reduction with chained Burst final reduce.
        /// </summary>
        public static ChannelSums ComputeParallelBurstRed(NativeArray<Color32> pixels, int chunkSize, int batchCount = DefaultInnerLoopBatchCount)
        {
            int chunkCount = (pixels.Length + chunkSize - 1) / chunkSize;
            using var partials = new NativeArray<PaddedChannelSum>(chunkCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            using var result = new NativeArray<ChannelSums>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var mapJob = new ParallelBurstRedSumJob
            {
                Pixels = pixels,
                PartialSums = partials,
                ChunkSize = chunkSize
            };

            var reduceJob = new FinalReduceChannelSumsJob
            {
                PartialSums = partials,
                Result = result
            };

            JobHandle mapHandle = mapJob.Schedule(chunkCount, batchCount);
            JobHandle reduceHandle = reduceJob.Schedule(mapHandle);
            reduceHandle.Complete();

            return result[0];
        }

        /// <summary>
        /// Extended Unity Jobs + Burst parallel reduction summing R, G, B, and A channels concurrently.
        /// </summary>
        public static ChannelSums ComputeParallelBurstRgba(NativeArray<Color32> pixels, int chunkSize, int batchCount = DefaultInnerLoopBatchCount)
        {
            int chunkCount = (pixels.Length + chunkSize - 1) / chunkSize;
            using var partials = new NativeArray<PaddedChannelSum>(chunkCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            using var result = new NativeArray<ChannelSums>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var mapJob = new ParallelBurstRgbaSumJob
            {
                Pixels = pixels,
                PartialSums = partials,
                ChunkSize = chunkSize
            };

            var reduceJob = new FinalReduceChannelSumsJob
            {
                PartialSums = partials,
                Result = result
            };

            JobHandle mapHandle = mapJob.Schedule(chunkCount, batchCount);
            JobHandle reduceHandle = reduceJob.Schedule(mapHandle);
            reduceHandle.Complete();

            return result[0];
        }

        /// <summary>
        /// Processes multiple images concurrently by scheduling all Burst map+reduce job chains
        /// simultaneously and completing their combined dependency graph in parallel.
        /// </summary>
        public static ChannelSums ComputeMultiImageConcurrentBurst(IReadOnlyList<LoadedDiskImage> images, int batchCount = DefaultInnerLoopBatchCount)
        {
            int count = images.Count;
            var partialBuffers = new NativeArray<PaddedChannelSum>[count];
            var resultBuffers = new NativeArray<ChannelSums>[count];
            var handles = new NativeArray<JobHandle>(count, Allocator.Temp);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    NativeArray<Color32> pixels = images[i].NativePixels;
                    int chunkSize = ComputeAdaptiveChunkSize(pixels.Length);
                    int chunkCount = (pixels.Length + chunkSize - 1) / chunkSize;

                    partialBuffers[i] = new NativeArray<PaddedChannelSum>(chunkCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    resultBuffers[i] = new NativeArray<ChannelSums>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    var mapJob = new ParallelBurstRedSumJob
                    {
                        Pixels = pixels,
                        PartialSums = partialBuffers[i],
                        ChunkSize = chunkSize
                    };

                    var reduceJob = new FinalReduceChannelSumsJob
                    {
                        PartialSums = partialBuffers[i],
                        Result = resultBuffers[i]
                    };

                    JobHandle mapHandle = mapJob.Schedule(chunkCount, batchCount);
                    handles[i] = reduceJob.Schedule(mapHandle);
                }

                // Kick off all queued jobs across worker threads immediately and wait for combined completion
                JobHandle.ScheduleBatchedJobs();
                JobHandle combined = JobHandle.CombineDependencies(handles);
                combined.Complete();

                ulong totalR = 0;
                for (int i = 0; i < count; i++)
                {
                    totalR += resultBuffers[i][0].r;
                }
                return new ChannelSums(totalR);
            }
            finally
            {
                if (handles.IsCreated) handles.Dispose();
                for (int i = 0; i < count; i++)
                {
                    if (partialBuffers[i].IsCreated) partialBuffers[i].Dispose();
                    if (resultBuffers[i].IsCreated) resultBuffers[i].Dispose();
                }
            }
        }

        /// <summary>
        /// Runs all 5 single-image execution strategies on a loaded disk image and returns verified telemetry records.
        /// </summary>
        public static List<BenchmarkEntry> BenchmarkSingleImageAllStrategies(
            LoadedDiskImage image,
            int warmupIterations = DefaultWarmupIterations,
            int measuredIterations = DefaultMeasuredIterations,
            int? overrideChunkSize = null)
        {
            int chunkSize = overrideChunkSize ?? ComputeAdaptiveChunkSize(image.NativePixels.Length);
            ChannelSums referenceRgba = ComputeReferenceRgba(image.ManagedPixels);

            var results = new List<BenchmarkEntry>(5);

            // 1. Single-threaded Managed C# baseline
            BenchmarkEntry managed = RunTimedBenchmark(
                image,
                AnalysisStrategy.SingleThreadManaged,
                chunkSize: 0,
                batchCount: 0,
                warmupIterations,
                measuredIterations,
                referenceRgba,
                checkAllChannels: false,
                () => ComputeSingleThreadManagedRed(image.ManagedPixels));
            managed.speedupVsManaged = 1.0;
            results.Add(managed);

            double baselineMedianMs = managed.medianMs;

            // 2. Single-threaded Burst IJob
            BenchmarkEntry singleBurst = RunTimedBenchmark(
                image,
                AnalysisStrategy.SingleThreadBurst,
                chunkSize: 0,
                batchCount: 0,
                warmupIterations,
                measuredIterations,
                referenceRgba,
                checkAllChannels: false,
                () => ComputeSingleThreadBurstRed(image.NativePixels));
            singleBurst.speedupVsManaged = singleBurst.medianMs > 0 ? baselineMedianMs / singleBurst.medianMs : 0;
            results.Add(singleBurst);

            // 3. Parallel IJobParallelFor WITHOUT Burst
            BenchmarkEntry parallelNoBurst = RunTimedBenchmark(
                image,
                AnalysisStrategy.ParallelNoBurst,
                chunkSize,
                DefaultInnerLoopBatchCount,
                warmupIterations,
                measuredIterations,
                referenceRgba,
                checkAllChannels: false,
                () => ComputeParallelNoBurstRed(image.NativePixels, chunkSize));
            parallelNoBurst.speedupVsManaged = parallelNoBurst.medianMs > 0 ? baselineMedianMs / parallelNoBurst.medianMs : 0;
            results.Add(parallelNoBurst);

            // 4. Parallel IJobParallelFor + Burst (Red channel — primary requirement)
            BenchmarkEntry parallelBurstRed = RunTimedBenchmark(
                image,
                AnalysisStrategy.ParallelBurstRed,
                chunkSize,
                DefaultInnerLoopBatchCount,
                warmupIterations,
                measuredIterations,
                referenceRgba,
                checkAllChannels: false,
                () => ComputeParallelBurstRed(image.NativePixels, chunkSize));
            parallelBurstRed.speedupVsManaged = parallelBurstRed.medianMs > 0 ? baselineMedianMs / parallelBurstRed.medianMs : 0;
            results.Add(parallelBurstRed);

            // 5. Parallel IJobParallelFor + Burst (All RGBA channels concurrently)
            BenchmarkEntry parallelBurstRgba = RunTimedBenchmark(
                image,
                AnalysisStrategy.ParallelBurstRgba,
                chunkSize,
                DefaultInnerLoopBatchCount,
                warmupIterations,
                measuredIterations,
                referenceRgba,
                checkAllChannels: true,
                () => ComputeParallelBurstRgba(image.NativePixels, chunkSize));
            parallelBurstRgba.speedupVsManaged = parallelBurstRgba.medianMs > 0 ? baselineMedianMs / parallelBurstRgba.medianMs : 0;
            results.Add(parallelBurstRgba);

            return results;
        }

        /// <summary>
        /// Benchmarks sequential single-threaded vs. concurrent Burst Jobs across a batch of multiple images.
        /// </summary>
        public static List<BenchmarkEntry> BenchmarkMultiImageBatch(
            IReadOnlyList<LoadedDiskImage> images,
            int warmupIterations = DefaultWarmupIterations,
            int measuredIterations = DefaultMeasuredIterations)
        {
            if (images == null || images.Count == 0)
                throw new ArgumentException("Image list cannot be empty.", nameof(images));

            long totalPixels = 0;
            double totalLoadMs = 0;
            ulong refRed = 0;
            for (int i = 0; i < images.Count; i++)
            {
                totalPixels += images[i].PixelCount;
                totalLoadMs += images[i].TotalLoadMs;
                refRed += ComputeSingleThreadManagedRed(images[i].ManagedPixels).r;
            }

            string batchLabel = $"Batch_{images.Count}x_{images[0].Width}x{images[0].Height}";
            var refSums = new ChannelSums(refRed);

            BenchmarkEntry seqEntry = RunTimedBatchBenchmark(
                batchLabel,
                images[0].Width,
                images[0].Height * images.Count,
                totalPixels,
                images[0].SourceFormat.ToString(),
                totalLoadMs,
                AnalysisStrategy.MultiImageSequentialManaged,
                chunkSize: 0,
                batchCount: 0,
                warmupIterations,
                measuredIterations,
                refSums,
                () =>
                {
                    ulong sum = 0;
                    for (int i = 0; i < images.Count; i++)
                        sum += ComputeSingleThreadManagedRed(images[i].ManagedPixels).r;
                    return new ChannelSums(sum);
                });
            seqEntry.speedupVsManaged = 1.0;

            int adaptiveChunk = ComputeAdaptiveChunkSize(images[0].NativePixels.Length);
            BenchmarkEntry concurrentEntry = RunTimedBatchBenchmark(
                batchLabel,
                images[0].Width,
                images[0].Height * images.Count,
                totalPixels,
                images[0].SourceFormat.ToString(),
                totalLoadMs,
                AnalysisStrategy.MultiImageConcurrentBurst,
                chunkSize: adaptiveChunk,
                batchCount: DefaultInnerLoopBatchCount,
                warmupIterations,
                measuredIterations,
                refSums,
                () => ComputeMultiImageConcurrentBurst(images));

            concurrentEntry.speedupVsManaged = concurrentEntry.medianMs > 0
                ? seqEntry.medianMs / concurrentEntry.medianMs
                : 0;

            return new List<BenchmarkEntry> { seqEntry, concurrentEntry };
        }

        private static BenchmarkEntry RunTimedBenchmark(
            LoadedDiskImage image,
            AnalysisStrategy strategy,
            int chunkSize,
            int batchCount,
            int warmupIterations,
            int measuredIterations,
            ChannelSums reference,
            bool checkAllChannels,
            Func<ChannelSums> action)
        {
            return RunTimedBatchBenchmark(
                image.FileName,
                image.Width,
                image.Height,
                image.PixelCount,
                image.SourceFormat.ToString(),
                image.TotalLoadMs,
                strategy,
                chunkSize,
                batchCount,
                warmupIterations,
                measuredIterations,
                reference,
                action,
                checkAllChannels);
        }

        private static BenchmarkEntry RunTimedBatchBenchmark(
            string imageName,
            int width,
            int height,
            long pixelCount,
            string textureFormat,
            double diskLoadMs,
            AnalysisStrategy strategy,
            int chunkSize,
            int batchCount,
            int warmupIterations,
            int measuredIterations,
            ChannelSums reference,
            Func<ChannelSums> action,
            bool checkAllChannels = false)
        {
            // Warmup runs to trigger Burst JIT compilation and warm caches
            ChannelSums lastResult = default;
            for (int w = 0; w < warmupIterations; w++)
            {
                lastResult = action();
            }

            double ticksToMs = 1000.0 / Stopwatch.Frequency;
            double[] samples = new double[measuredIterations];

            for (int m = 0; m < measuredIterations; m++)
            {
                long start = Stopwatch.GetTimestamp();
                lastResult = action();
                long end = Stopwatch.GetTimestamp();
                samples[m] = (end - start) * ticksToMs;
            }

            Array.Sort(samples);
            double min = samples[0];
            double max = samples[samples.Length - 1];
            double median = (samples.Length % 2 == 1)
                ? samples[samples.Length / 2]
                : 0.5 * (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]);

            double sum = 0;
            for (int i = 0; i < samples.Length; i++) sum += samples[i];
            double mean = sum / samples.Length;

            double varianceSum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double diff = samples[i] - mean;
                varianceSum += diff * diff;
            }
            double stdDev = Math.Sqrt(varianceSum / samples.Length);

            double throughputMPixPerSec = median > 0
                ? (pixelCount / 1_000_000.0) / (median / 1000.0)
                : 0;

            bool matches = checkAllChannels
                ? (lastResult.r == reference.r && lastResult.g == reference.g && lastResult.b == reference.b && lastResult.a == reference.a)
                : (lastResult.r == reference.r);

            return new BenchmarkEntry
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                imageName = imageName,
                width = width,
                height = height,
                pixelCount = pixelCount,
                textureFormat = textureFormat,
                diskLoadMs = Math.Round(diskLoadMs, 4),
                strategy = strategy.ToString(),
                chunkSize = chunkSize,
                innerLoopBatchCount = batchCount,
                warmupIterations = warmupIterations,
                measuredIterations = measuredIterations,
                minMs = Math.Round(min, 5),
                medianMs = Math.Round(median, 5),
                meanMs = Math.Round(mean, 5),
                maxMs = Math.Round(max, 5),
                stdDevMs = Math.Round(stdDev, 5),
                throughputMPixPerSec = Math.Round(throughputMPixPerSec, 2),
                speedupVsManaged = 1.0,
                redSum = lastResult.r,
                greenSum = lastResult.g,
                blueSum = lastResult.b,
                alphaSum = lastResult.a,
                matchesReference = matches
            };
        }

        public static BenchmarkReport CreateReport(List<BenchmarkEntry> entries)
        {
            return new BenchmarkReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("o"),
                unityVersion = Application.unityVersion,
                runtimeEnvironment = Application.isEditor ? "Unity Editor" : $"Standalone ({Application.platform})",
                processorType = SystemInfo.processorType,
                logicalProcessorCount = SystemInfo.processorCount,
                jobWorkerCount = JobsUtility.JobWorkerCount,
                burstEnabled = BurstCompiler.Options.EnableBurstCompilation,
                systemMemoryMB = SystemInfo.systemMemorySize,
                entries = entries ?? new List<BenchmarkEntry>()
            };
        }
    }
}
