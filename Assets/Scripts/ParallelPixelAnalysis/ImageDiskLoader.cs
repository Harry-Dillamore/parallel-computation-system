using System;
using System.Diagnostics;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// Holds a texture loaded from disk along with its RGBA32 NativeArray view
    /// for Burst/Jobs processing and a managed Color32 array for single-thread baseline comparison.
    /// </summary>
    public sealed class LoadedDiskImage : IDisposable
    {
        public string FilePath { get; }
        public string FileName { get; }
        public Texture2D Texture { get; private set; }
        public int Width { get; }
        public int Height { get; }
        public long PixelCount => (long)Width * Height;
        public TextureFormat SourceFormat { get; }
        public double DiskReadMs { get; }
        public double DecodeMs { get; }
        public double TotalLoadMs => DiskReadMs + DecodeMs;
        public bool IsZeroCopyNativeView { get; }

        /// <summary>
        /// RGBA32 pixel buffer ready for Burst-compiled Unity Jobs.
        /// </summary>
        public NativeArray<Color32> NativePixels { get; private set; }

        /// <summary>
        /// Managed RGBA32 pixel array used exclusively by the single-threaded managed C# baseline
        /// so both baselines operate on contiguous, pre-loaded RGBA32 memory.
        /// </summary>
        public Color32[] ManagedPixels { get; private set; }

        private bool _ownsNativePixels;
        private bool _disposed;

        internal LoadedDiskImage(
            string filePath,
            Texture2D texture,
            NativeArray<Color32> nativePixels,
            bool ownsNativePixels,
            Color32[] managedPixels,
            double diskReadMs,
            double decodeMs)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            Texture = texture;
            Width = texture.width;
            Height = texture.height;
            SourceFormat = texture.format;
            NativePixels = nativePixels;
            _ownsNativePixels = ownsNativePixels;
            IsZeroCopyNativeView = !ownsNativePixels;
            ManagedPixels = managedPixels;
            DiskReadMs = diskReadMs;
            DecodeMs = decodeMs;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ownsNativePixels && NativePixels.IsCreated)
            {
                NativePixels.Dispose();
            }

            if (Texture != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(Texture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(Texture);
                }
                Texture = null;
            }

            ManagedPixels = null;
        }
    }

    /// <summary>
    /// Loads PNG/JPG textures directly from disk into Unity Texture2D and RGBA32 buffers.
    /// </summary>
    public static class ImageDiskLoader
    {
        public static LoadedDiskImage LoadFromDisk(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Image file not found on disk: {filePath}", filePath);

            double ticksToMs = 1000.0 / Stopwatch.Frequency;

            // 1. Read raw encoded image bytes from disk
            long t0 = Stopwatch.GetTimestamp();
            byte[] fileBytes = File.ReadAllBytes(filePath);
            long t1 = Stopwatch.GetTimestamp();
            double diskReadMs = (t1 - t0) * ticksToMs;

            // 2. Decode PNG/JPG into a readable RGBA32 Texture2D (no mipmaps for crisp 1:1 analysis)
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = Path.GetFileName(filePath),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            long t2 = Stopwatch.GetTimestamp();
            bool loaded = texture.LoadImage(fileBytes, markNonReadable: false);
            long t3 = Stopwatch.GetTimestamp();
            double decodeMs = (t3 - t2) * ticksToMs;

            if (!loaded)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException($"Texture2D.LoadImage failed to decode: {filePath}");
            }

            // 3. Acquire RGBA32 NativeArray<Color32> for Burst Jobs.
            // If decoded as RGBA32, GetRawTextureData<Color32>() gives a zero-copy native view directly into texture memory.
            // If decoded as RGB24/BGRA32/etc., normalize once via GetPixels32() to guarantee exact R/G/B/A byte alignment.
            NativeArray<Color32> nativePixels;
            bool ownsNativePixels;
            Color32[] managedPixels = texture.GetPixels32();

            if (texture.format == TextureFormat.RGBA32)
            {
                nativePixels = texture.GetRawTextureData<Color32>();
                ownsNativePixels = false;
            }
            else
            {
                nativePixels = new NativeArray<Color32>(managedPixels, Allocator.Persistent);
                ownsNativePixels = true;
            }

            return new LoadedDiskImage(
                filePath,
                texture,
                nativePixels,
                ownsNativePixels,
                managedPixels,
                diskReadMs,
                decodeMs);
        }

        public static string GetDefaultTestImagesDirectory()
        {
            return Path.Combine(Application.streamingAssetsPath, "TestImages");
        }
    }
}
