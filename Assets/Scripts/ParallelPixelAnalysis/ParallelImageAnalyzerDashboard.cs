using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Burst;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// Interactive runtime dashboard and benchmark controller for the Parallel Pixel Computation System.
    /// Loads PNG/JPG images from disk, executes single-threaded and Unity Jobs + Burst strategies,
    /// visualizes execution time & speedup live, logs structured results, and exports CSV/JSON telemetry.
    /// </summary>
    public class ParallelImageAnalyzerDashboard : MonoBehaviour
    {
        [Header("Benchmark Settings")]
        [SerializeField] private int warmupIterations = PixelSumBenchmarkRunner.DefaultWarmupIterations;
        [SerializeField] private int measuredIterations = PixelSumBenchmarkRunner.DefaultMeasuredIterations;
        [SerializeField] private bool runInitialBenchmarkOnStart = true;

        private readonly List<string> _availableImagePaths = new List<string>();
        private readonly List<string> _batchImagePaths = new List<string>();
        private int _selectedImageIndex = -1;

        private LoadedDiskImage _activeImage;
        private readonly List<BenchmarkEntry> _activeEntries = new List<BenchmarkEntry>();
        private readonly List<BenchmarkEntry> _allSessionEntries = new List<BenchmarkEntry>();
        private readonly List<string> _logLines = new List<string>();

        private string _statusBanner = "Ready";
        private string _activeChartTitle = "Execution Time Comparison (Median ms)";
        private string _customPathInput = "";
        private Vector2 _logScrollPos;
        private bool _isBusy;

        // Cached GUI styles & solid textures
        private bool _stylesInitialized;
        private Texture2D _texBgDark;
        private Texture2D _texPanel;
        private Texture2D _texPanelAlt;
        private Texture2D _texBorder;
        private Texture2D _texWhite;
        private GUIStyle _styleHeader;
        private GUIStyle _styleSubHeader;
        private GUIStyle _styleCard;
        private GUIStyle _styleCardTitle;
        private GUIStyle _styleLabel;
        private GUIStyle _styleLabelMuted;
        private GUIStyle _styleLabelBold;
        private GUIStyle _styleLogLine;
        private GUIStyle _styleButton;
        private GUIStyle _styleButtonAccent;
        private GUIStyle _styleBadgeOk;
        private GUIStyle _styleBadgeWarn;

        private static readonly Color ColManaged = new Color(0.94f, 0.36f, 0.36f, 1f);
        private static readonly Color ColParallelNoBurst = new Color(0.96f, 0.65f, 0.20f, 1f);
        private static readonly Color ColSingleBurst = new Color(0.25f, 0.72f, 0.96f, 1f);
        private static readonly Color ColParallelBurstRed = new Color(0.18f, 0.84f, 0.52f, 1f);
        private static readonly Color ColParallelBurstRgba = new Color(0.67f, 0.48f, 0.98f, 1f);

        private void Start()
        {
            // Ensure camera clears cleanly behind the dashboard
            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.06f, 0.09f, 0.16f, 1f);
            }

            DiscoverDiskImages();

            if (HasCommandLineFlag("--auto-demo"))
            {
                StartCoroutine(RunAutoDemoCaptureSequence());
            }
            else if (runInitialBenchmarkOnStart && _availableImagePaths.Count > 0)
            {
                // Default to 2048x2048 if present, otherwise first discovered image
                int defaultIdx = _availableImagePaths.FindIndex(p => p.Contains("2048"));
                if (defaultIdx < 0) defaultIdx = 0;
                SelectAndBenchmarkImage(defaultIdx);
            }
        }

        private void OnDestroy()
        {
            _activeImage?.Dispose();
            _activeImage = null;

            DestroyTex(ref _texBgDark);
            DestroyTex(ref _texPanel);
            DestroyTex(ref _texPanelAlt);
            DestroyTex(ref _texBorder);
            DestroyTex(ref _texWhite);
        }

        private static void DestroyTex(ref Texture2D tex)
        {
            if (tex != null)
            {
                Destroy(tex);
                tex = null;
            }
        }

        private void DiscoverDiskImages()
        {
            _availableImagePaths.Clear();
            _batchImagePaths.Clear();

            string dir = ImageDiskLoader.GetDefaultTestImagesDirectory();
            if (!Directory.Exists(dir))
            {
                AppendLog($"[WARN] StreamingAssets TestImages directory not found: {dir}");
                return;
            }

            string[] files = Directory.GetFiles(dir, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            // Order test_512 -> 1024 -> 2048 -> 4096 nicely
            string[] preferredOrder = { "test_512x512.png", "test_1024x1024.png", "test_2048x2048.png", "test_4096x4096.png" };
            foreach (string name in preferredOrder)
            {
                string match = Array.Find(files, f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                if (match != null) _availableImagePaths.Add(match);
            }

            foreach (string f in files)
            {
                string fname = Path.GetFileName(f);
                if (fname.StartsWith("batch_", StringComparison.OrdinalIgnoreCase))
                {
                    _batchImagePaths.Add(f);
                }
                else if (!_availableImagePaths.Contains(f))
                {
                    _availableImagePaths.Add(f);
                }
            }

            AppendLog($"System Ready | CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} logical cores, {JobsUtility.JobWorkerCount} Job Workers)");
            AppendLog($"Unity {Application.unityVersion} | Burst Compilation: {(BurstCompiler.Options.EnableBurstCompilation ? "ENABLED" : "DISABLED")} | Discovered {_availableImagePaths.Count} primary + {_batchImagePaths.Count} batch images on disk.");
        }

        private void SelectAndBenchmarkImage(int index)
        {
            if (index < 0 || index >= _availableImagePaths.Count) return;
            _selectedImageIndex = index;
            LoadAndBenchmarkPath(_availableImagePaths[index]);
        }

        private void LoadAndBenchmarkPath(string filePath)
        {
            try
            {
                _isBusy = true;
                _statusBanner = $"Loading {Path.GetFileName(filePath)} from disk...";

                _activeImage?.Dispose();
                _activeImage = ImageDiskLoader.LoadFromDisk(filePath);
                _customPathInput = filePath;

                AppendLog($"Loaded '{_activeImage.FileName}' from disk ({_activeImage.Width}x{_activeImage.Height}, {_activeImage.PixelCount:N0} px, {_activeImage.SourceFormat}, ZeroCopy={_activeImage.IsZeroCopyNativeView}) in {_activeImage.TotalLoadMs:F2} ms (Read {_activeImage.DiskReadMs:F2} ms + Decode {_activeImage.DecodeMs:F2} ms).");

                _statusBanner = $"Running 5 strategies on {_activeImage.FileName}...";
                List<BenchmarkEntry> results = PixelSumBenchmarkRunner.BenchmarkSingleImageAllStrategies(
                    _activeImage, warmupIterations, measuredIterations);

                _activeEntries.Clear();
                _activeEntries.AddRange(results);
                _allSessionEntries.AddRange(results);
                _activeChartTitle = $"Single-Image Comparison — {_activeImage.FileName} ({_activeImage.Width}x{_activeImage.Height}, {measuredIterations} runs)";

                LogEntriesDetailed(results);
                _statusBanner = $"Completed {_activeImage.FileName} — Burst Parallel Red: {results[3].medianMs:F4} ms ({results[3].speedupVsManaged:F2}x speedup)";
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {ex.Message}");
                _statusBanner = $"Error: {ex.Message}";
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void RunFullScalingSuite()
        {
            if (_availableImagePaths.Count == 0)
            {
                AppendLog("[WARN] No test images found in StreamingAssets/TestImages.");
                return;
            }

            _isBusy = true;
            _allSessionEntries.Clear();
            AppendLog("=== Starting Full Resolution Scaling Suite (512 -> 4096) ===");

            for (int i = 0; i < _availableImagePaths.Count; i++)
            {
                SelectAndBenchmarkImage(i);
            }

            BenchmarkReport report = PixelSumBenchmarkRunner.CreateReport(_allSessionEntries);
            TelemetryExporter.LogSummaryToConsole(report);
            _statusBanner = $"Full Scaling Suite Complete ({_allSessionEntries.Count} benchmark records)";
            _isBusy = false;
        }

        private void RunMultiImageBatchBenchmark()
        {
            if (_batchImagePaths.Count == 0)
            {
                AppendLog("[WARN] No batch_*.png images found for multi-image benchmark.");
                return;
            }

            _isBusy = true;
            var loadedBatch = new List<LoadedDiskImage>(_batchImagePaths.Count);
            try
            {
                AppendLog($"=== Loading {_batchImagePaths.Count} images from disk for Concurrent Multi-Image Batch ===");
                double totalDiskMs = 0;
                foreach (string path in _batchImagePaths)
                {
                    LoadedDiskImage img = ImageDiskLoader.LoadFromDisk(path);
                    loadedBatch.Add(img);
                    totalDiskMs += img.TotalLoadMs;
                }

                // Keep first batch image visible in preview and clear single-image button selection
                _selectedImageIndex = -1;
                _activeImage?.Dispose();
                _activeImage = ImageDiskLoader.LoadFromDisk(_batchImagePaths[0]);

                AppendLog($"Loaded {loadedBatch.Count}x {loadedBatch[0].Width}x{loadedBatch[0].Height} images ({loadedBatch.Count * loadedBatch[0].PixelCount:N0} total pixels) in {totalDiskMs:F2} ms.");

                List<BenchmarkEntry> batchResults = PixelSumBenchmarkRunner.BenchmarkMultiImageBatch(
                    loadedBatch, warmupIterations, measuredIterations);

                _activeEntries.Clear();
                _activeEntries.AddRange(batchResults);
                _allSessionEntries.AddRange(batchResults);
                _activeChartTitle = $"Multi-Image Concurrent Batch — {loadedBatch.Count}x {loadedBatch[0].Width}x{loadedBatch[0].Height} ({loadedBatch.Count * loadedBatch[0].PixelCount:N0} px)";

                LogEntriesDetailed(batchResults);
                _statusBanner = $"Multi-Image Batch Complete — Concurrent Burst: {batchResults[1].medianMs:F4} ms ({batchResults[1].speedupVsManaged:F2}x speedup)";
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] Batch benchmark failed: {ex.Message}");
                _statusBanner = $"Batch Error: {ex.Message}";
            }
            finally
            {
                foreach (var img in loadedBatch) img.Dispose();
                _isBusy = false;
            }
        }

        private void ExportSessionTelemetry()
        {
            if (_allSessionEntries.Count == 0)
            {
                AppendLog("[WARN] No telemetry recorded yet. Run a benchmark first.");
                return;
            }

            BenchmarkReport report = PixelSumBenchmarkRunner.CreateReport(_allSessionEntries);
            var (csvPath, jsonPath) = TelemetryExporter.ExportAll(report);
            TelemetryExporter.LogSummaryToConsole(report);
            AppendLog($"Exported {_allSessionEntries.Count} records -> {Path.GetFileName(csvPath)} & {Path.GetFileName(jsonPath)}");
            _statusBanner = $"Telemetry exported to {Path.GetDirectoryName(csvPath)}";
        }

        private void LogEntriesDetailed(List<BenchmarkEntry> entries)
        {
            foreach (BenchmarkEntry e in entries)
            {
                string line = string.Format(CultureInfo.InvariantCulture,
                    "[{0,-27}] Median: {1,8:F4} ms | Mean: {2,8:F4} ms | {3,8:N1} MPix/s | {4,6:F2}x | R={5:N0} | {6}",
                    e.strategy, e.medianMs, e.meanMs, e.throughputMPixPerSec, e.speedupVsManaged, e.redSum,
                    e.matchesReference ? "VALID" : "MISMATCH");
                AppendLog(line);
                Debug.Log($"[ParallelPixelAnalysis] {e.imageName} {line}");
            }
        }

        private void AppendLog(string message)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logLines.Add(stamped);
            if (_logLines.Count > 200)
            {
                _logLines.RemoveAt(0);
            }
            _logScrollPos.y = float.MaxValue;
        }

        /// <summary>
        /// Automated capture mode used when launched with --auto-demo to produce real screenshots,
        /// animation frames for GIF creation, and full CSV/JSON telemetry from the compiled Standalone player.
        /// </summary>
        private IEnumerator RunAutoDemoCaptureSequence()
        {
            string captureDir = GetCommandLineArgValue("--capture-dir") ??
                                Path.Combine(TelemetryExporter.GetDefaultExportDirectory(), "Captures");
            Directory.CreateDirectory(captureDir);

            int frameIdx = 0;
            IEnumerator Capture(string label, int repeatFrames = 1)
            {
                yield return new WaitForEndOfFrame();
                for (int r = 0; r < repeatFrames; r++)
                {
                    string file = Path.Combine(captureDir, $"frame_{frameIdx:D3}_{label}.png");
                    ScreenCapture.CaptureScreenshot(file);
                    frameIdx++;
                    yield return new WaitForEndOfFrame();
                }
            }

            // Let window initialize and render first clean frame
            yield return new WaitForSeconds(0.4f);

            _allSessionEntries.Clear();

            // Step through each resolution image: 512, 1024, 2048, 4096
            for (int i = 0; i < _availableImagePaths.Count; i++)
            {
                SelectAndBenchmarkImage(i);
                yield return new WaitForSeconds(0.15f);
                yield return Capture($"img_{i}_{Path.GetFileNameWithoutExtension(_availableImagePaths[i])}", repeatFrames: 1);
            }

            // Capture dedicated clean screenshot of 4096x4096 results
            string shot4k = Path.Combine(captureDir, "screenshot_dashboard_4096.png");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(shot4k);
            yield return new WaitForSeconds(0.2f);

            // Run multi-image concurrent batch benchmark
            RunMultiImageBatchBenchmark();
            yield return new WaitForSeconds(0.15f);
            yield return Capture("batch_4x2048", repeatFrames: 1);

            string shotBatch = Path.Combine(captureDir, "screenshot_batch_4x2048.png");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(shotBatch);
            yield return new WaitForSeconds(0.2f);

            // Export CSV & JSON telemetry
            ExportSessionTelemetry();
            yield return new WaitForSeconds(0.2f);
            yield return Capture("exported", repeatFrames: 1);

            // Wait briefly so async ScreenCapture writes flush cleanly to disk
            yield return new WaitForSeconds(0.5f);
            Application.Quit(0);
        }

        private static bool HasCommandLineFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetCommandLineArgValue(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        // =========================================================================
        // Runtime Dashboard GUI Rendering (Zero external asset dependencies)
        // =========================================================================

        private void EnsureStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _texBgDark = MakeSolidTexture(new Color(0.06f, 0.09f, 0.16f, 1f));
            _texPanel = MakeSolidTexture(new Color(0.11f, 0.15f, 0.24f, 1f));
            _texPanelAlt = MakeSolidTexture(new Color(0.08f, 0.11f, 0.18f, 1f));
            _texBorder = MakeSolidTexture(new Color(0.22f, 0.29f, 0.42f, 1f));
            _texWhite = MakeSolidTexture(Color.white);

            _styleHeader = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.95f, 0.98f, 1f, 1f) }
            };

            _styleSubHeader = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.65f, 0.76f, 0.90f, 1f) }
            };

            _styleCard = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texPanel },
                padding = new RectOffset(12, 12, 10, 10)
            };

            _styleCardTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.56f, 0.85f, 1f, 1f) }
            };

            _styleLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.90f, 0.94f, 0.98f, 1f) }
            };

            _styleLabelMuted = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = new Color(0.62f, 0.70f, 0.82f, 1f) }
            };

            _styleLabelBold = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _styleLogLine = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = false,
                normal = { textColor = new Color(0.80f, 0.92f, 0.84f, 1f) }
            };

            _styleButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(10, 10, 6, 6),
                normal = { textColor = Color.white }
            };

            _styleButtonAccent = new GUIStyle(_styleButton)
            {
                normal = { textColor = new Color(0.55f, 0.98f, 0.75f, 1f) }
            };

            _styleBadgeOk = new GUIStyle(GUI.skin.box)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal =
                {
                    background = MakeSolidTexture(new Color(0.09f, 0.42f, 0.27f, 1f)),
                    textColor = new Color(0.75f, 1f, 0.85f, 1f)
                }
            };

            _styleBadgeWarn = new GUIStyle(_styleBadgeOk)
            {
                normal =
                {
                    background = MakeSolidTexture(new Color(0.55f, 0.16f, 0.16f, 1f)),
                    textColor = Color.white
                }
            };
        }

        private static Texture2D MakeSolidTexture(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            EnsureStyles();

            float w = Screen.width;
            float h = Screen.height;
            GUI.DrawTexture(new Rect(0, 0, w, h), _texBgDark);

            const float pad = 12f;
            float topBarH = 68f;
            float toolbarH = 40f;

            // 1. Header Bar
            DrawHeaderBar(new Rect(pad, pad, w - pad * 2f, topBarH));

            // 2. Interactive Controls Toolbar
            DrawToolbar(new Rect(pad, pad + topBarH + 8f, w - pad * 2f, toolbarH));

            // 3. Main Content Columns
            float contentY = pad + topBarH + toolbarH + 16f;
            float contentH = h - contentY - pad;
            float leftW = Mathf.Clamp(w * 0.34f, 340f, 480f);
            float rightX = pad + leftW + 12f;
            float rightW = w - rightX - pad;

            DrawLeftImagePanel(new Rect(pad, contentY, leftW, contentH));

            float chartH = Mathf.Max(contentH * 0.56f, 260f);
            float logY = contentY + chartH + 10f;
            float logH = contentH - chartH - 10f;

            DrawChartPanel(new Rect(rightX, contentY, rightW, chartH));
            DrawLogPanel(new Rect(rightX, logY, rightW, logH));
        }

        private void DrawHeaderBar(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _styleCard);
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 8, rect.width - 24, rect.height - 12));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("PARALLEL PIXEL COMPUTATION SYSTEM — UNITY JOBS + BURST", _styleHeader);
            GUILayout.Space(2);
            string burstState = BurstCompiler.Options.EnableBurstCompilation ? "BURST ENABLED" : "BURST DISABLED";
            GUILayout.Label(
                $"CPU: {SystemInfo.processorType.Trim()}  |  {SystemInfo.processorCount} Logical Cores  |  {JobsUtility.JobWorkerCount} Job Workers  |  {burstState}  |  Unity {Application.unityVersion}",
                _styleSubHeader);
            GUILayout.EndVertical();
            GUILayout.Label(_statusBanner, _styleLabelBold, GUILayout.Width(390));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawToolbar(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _styleCard);
            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 6, rect.width - 20, rect.height - 12));
            GUILayout.BeginHorizontal();

            GUI.enabled = !_isBusy;
            GUILayout.Label("Image:", _styleLabelBold, GUILayout.Width(48));

            for (int i = 0; i < _availableImagePaths.Count; i++)
            {
                string shortName = Path.GetFileNameWithoutExtension(_availableImagePaths[i]).Replace("test_", "");
                GUIStyle btnStyle = (i == _selectedImageIndex) ? _styleButtonAccent : _styleButton;
                if (GUILayout.Button(shortName, btnStyle, GUILayout.Height(26), GUILayout.MinWidth(74)))
                {
                    SelectAndBenchmarkImage(i);
                }
            }

            GUILayout.Space(12);
            if (GUILayout.Button("Run Full Scaling Suite", _styleButtonAccent, GUILayout.Height(26)))
            {
                RunFullScalingSuite();
            }

            if (GUILayout.Button("Run 4x Multi-Image Batch", _styleButtonAccent, GUILayout.Height(26)))
            {
                RunMultiImageBatchBenchmark();
            }

            if (GUILayout.Button("Export CSV & JSON", _styleButton, GUILayout.Height(26)))
            {
                ExportSessionTelemetry();
            }

            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawLeftImagePanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _styleCard);
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 10, rect.width - 24, rect.height - 20));

            GUILayout.Label("DISK IMAGE PREVIEW & CHANNEL TOTALS", _styleCardTitle);
            GUILayout.Space(6);

            // Preview Box
            float previewSize = Mathf.Min(rect.width - 28f, rect.height * 0.44f);
            Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
            previewRect.x = (rect.width - 24f - previewSize) * 0.5f;
            GUI.DrawTexture(previewRect, _texPanelAlt);

            if (_activeImage != null && _activeImage.Texture != null)
            {
                GUI.DrawTexture(previewRect, _activeImage.Texture, ScaleMode.ScaleToFit);
            }

            GUILayout.Space(10);

            if (_activeImage != null)
            {
                GUILayout.Label($"File: {_activeImage.FileName}", _styleLabelBold);
                GUILayout.Label($"Dimensions: {_activeImage.Width} x {_activeImage.Height}  ({_activeImage.PixelCount:N0} pixels)", _styleLabel);
                GUILayout.Label($"Format: {_activeImage.SourceFormat}  (Zero-Copy NativeArray: {_activeImage.IsZeroCopyNativeView})", _styleLabelMuted);
                GUILayout.Label($"Disk Load + PNG Decode: {_activeImage.TotalLoadMs:F2} ms  (IO {_activeImage.DiskReadMs:F2} ms | Decode {_activeImage.DecodeMs:F2} ms)", _styleLabelMuted);

                GUILayout.Space(8);

                if (_activeEntries.Count > 0)
                {
                    BenchmarkEntry primary = _activeEntries.Find(e => e.strategy == AnalysisStrategy.ParallelBurstRed.ToString())
                                             ?? _activeEntries[_activeEntries.Count - 1];
                    BenchmarkEntry rgba = _activeEntries.Find(e => e.strategy == AnalysisStrategy.ParallelBurstRgba.ToString());

                    GUILayout.Label($"Red (R) Channel Sum:  {primary.redSum:N0}", _styleLabelBold);
                    if (rgba != null)
                    {
                        GUILayout.Label($"Green (G): {rgba.greenSum:N0}  |  Blue (B): {rgba.blueSum:N0}", _styleLabelMuted);
                        GUILayout.Label($"Alpha (A): {rgba.alphaSum:N0}", _styleLabelMuted);
                    }

                    GUILayout.Space(8);

                    bool allValid = _activeEntries.TrueForAll(e => e.matchesReference);
                    GUILayout.Box(
                        allValid ? "VERIFIED: BIT-EXACT MATCH VS SINGLE-THREAD BASELINE" : "WARNING: SUM MISMATCH DETECTED",
                        allValid ? _styleBadgeOk : _styleBadgeWarn,
                        GUILayout.Height(28));
                }
            }
            else
            {
                GUILayout.Label("No image loaded yet.", _styleLabelMuted);
            }

            GUILayout.EndArea();
        }

        private void DrawChartPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _styleCard);
            GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 10, rect.width - 28, rect.height - 20));

            GUILayout.Label(_activeChartTitle, _styleCardTitle);
            GUILayout.Space(6);

            if (_activeEntries.Count == 0)
            {
                GUILayout.Label("Run a benchmark to visualize execution times and speedup.", _styleLabelMuted);
                GUILayout.EndArea();
                return;
            }

            double maxMs = 0.0001;
            foreach (var e in _activeEntries)
            {
                if (e.medianMs > maxMs) maxMs = e.medianMs;
            }

            float availW = rect.width - 28f;
            float labelW = 190f;
            float metricW = 245f;
            float barAreaW = Mathf.Max(120f, availW - labelW - metricW - 16f);

            // Column header
            GUILayout.BeginHorizontal();
            GUILayout.Label("Strategy", _styleLabelMuted, GUILayout.Width(labelW));
            GUILayout.Label("Relative Execution Time (Lower is Faster)", _styleLabelMuted, GUILayout.Width(barAreaW));
            GUILayout.Label("Median | Speedup | Throughput", _styleLabelMuted, GUILayout.Width(metricW));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            foreach (BenchmarkEntry e in _activeEntries)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(36));

                GUILayout.Label(FormatFriendlyStrategyName(e.strategy), _styleLabelBold, GUILayout.Width(labelW));

                // Bar track + colored bar
                Rect trackRect = GUILayoutUtility.GetRect(barAreaW, 24, GUILayout.Width(barAreaW));
                trackRect.y += 4;
                GUI.DrawTexture(trackRect, _texPanelAlt);

                float fraction = Mathf.Clamp((float)(e.medianMs / maxMs), 0.015f, 1f);
                Rect fillRect = new Rect(trackRect.x, trackRect.y, trackRect.width * fraction, trackRect.height);

                Color oldColor = GUI.color;
                GUI.color = GetStrategyColor(e.strategy);
                GUI.DrawTexture(fillRect, _texWhite);
                GUI.color = oldColor;

                GUILayout.Space(8);
                string metricText = string.Format(CultureInfo.InvariantCulture,
                    "{0:F4} ms  |  {1,5:F1}x  |  {2:N0} MP/s",
                    e.medianMs, e.speedupVsManaged, e.throughputMPixPerSec);
                GUILayout.Label(metricText, _styleLabel, GUILayout.Width(metricW));

                GUILayout.EndHorizontal();
            }

            // Summary KPI highlight strip at bottom of chart card
            BenchmarkEntry fastest = _activeEntries[0];
            foreach (var e in _activeEntries)
            {
                if (e.medianMs < fastest.medianMs) fastest = e;
            }

            GUILayout.FlexibleSpace();
            GUI.DrawTexture(GUILayoutUtility.GetRect(availW, 1), _texBorder);
            GUILayout.Space(6);
            GUILayout.Label(
                string.Format(CultureInfo.InvariantCulture,
                    "Fastest: {0}  —  {1:F4} ms median ({2:F2}x faster than single-thread C#, {3:N1} MPix/sec, ChunkSize={4:N0} px)",
                    FormatFriendlyStrategyName(fastest.strategy),
                    fastest.medianMs,
                    fastest.speedupVsManaged,
                    fastest.throughputMPixPerSec,
                    fastest.chunkSize),
                _styleLabelBold);

            GUILayout.EndArea();
        }

        private void DrawLogPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _styleCard);
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 8, rect.width - 24, rect.height - 16));

            GUILayout.Label("EXECUTION LOG & TELEMETRY OUTPUT", _styleCardTitle);
            GUILayout.Space(4);

            Rect bgRect = new Rect(0, 24, rect.width - 24, rect.height - 42);
            GUI.DrawTexture(bgRect, _texPanelAlt);

            _logScrollPos = GUILayout.BeginScrollView(_logScrollPos, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _logLines.Count; i++)
            {
                GUILayout.Label(_logLines[i], _styleLogLine);
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private static string FormatFriendlyStrategyName(string strategy)
        {
            switch (strategy)
            {
                case nameof(AnalysisStrategy.SingleThreadManaged): return "1. Single-Thread (C#)";
                case nameof(AnalysisStrategy.SingleThreadBurst): return "2. Single-Thread + Burst";
                case nameof(AnalysisStrategy.ParallelNoBurst): return "3. Parallel Jobs (No Burst)";
                case nameof(AnalysisStrategy.ParallelBurstRed): return "4. Parallel + Burst (Red)";
                case nameof(AnalysisStrategy.ParallelBurstRgba): return "5. Parallel + Burst (RGBA)";
                case nameof(AnalysisStrategy.MultiImageSequentialManaged): return "1. Sequential 4x (C#)";
                case nameof(AnalysisStrategy.MultiImageConcurrentBurst): return "2. Concurrent 4x + Burst";
                default: return strategy;
            }
        }

        private static Color GetStrategyColor(string strategy)
        {
            switch (strategy)
            {
                case nameof(AnalysisStrategy.SingleThreadManaged):
                case nameof(AnalysisStrategy.MultiImageSequentialManaged):
                    return ColManaged;
                case nameof(AnalysisStrategy.ParallelNoBurst):
                    return ColParallelNoBurst;
                case nameof(AnalysisStrategy.SingleThreadBurst):
                    return ColSingleBurst;
                case nameof(AnalysisStrategy.ParallelBurstRed):
                case nameof(AnalysisStrategy.MultiImageConcurrentBurst):
                    return ColParallelBurstRed;
                case nameof(AnalysisStrategy.ParallelBurstRgba):
                    return ColParallelBurstRgba;
                default:
                    return Color.cyan;
            }
        }
    }
}
