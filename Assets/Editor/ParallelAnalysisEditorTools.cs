using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParallelPixelAnalysis;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParallelPixelAnalysis.Editor
{
    public static class ParallelAnalysisEditorTools
    {
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Tools/Parallel Pixel Analysis/1. Setup SampleScene")]
        public static void SetupSampleScene()
        {
            Scene scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);

            var existing = UnityEngine.Object.FindFirstObjectByType<ParallelImageAnalyzerDashboard>();
            if (existing == null)
            {
                var go = new GameObject("ParallelPixelAnalyzer");
                go.AddComponent<ParallelImageAnalyzerDashboard>();
                Debug.Log("[ParallelAnalysisEditorTools] Added ParallelPixelAnalyzer GameObject to SampleScene.");
            }
            else
            {
                Debug.Log("[ParallelAnalysisEditorTools] ParallelImageAnalyzerDashboard already present in SampleScene.");
            }

            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = new Color(0.06f, 0.09f, 0.16f, 1f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[ParallelAnalysisEditorTools] SampleScene saved successfully.");
        }

        [MenuItem("Tools/Parallel Pixel Analysis/2. Run Full Benchmark Suite (Editor)")]
        public static void RunFullBenchmarkSuiteInEditor()
        {
            string imgDir = ImageDiskLoader.GetDefaultTestImagesDirectory();
            if (!Directory.Exists(imgDir))
            {
                Debug.LogError($"[ParallelAnalysisEditorTools] Missing TestImages folder: {imgDir}");
                return;
            }

            string[] primaryNames =
            {
                "test_512x512.png",
                "test_1024x1024.png",
                "test_2048x2048.png",
                "test_4096x4096.png"
            };

            var allEntries = new List<BenchmarkEntry>();

            foreach (string name in primaryNames)
            {
                string path = Path.Combine(imgDir, name);
                if (!File.Exists(path)) continue;

                using var img = ImageDiskLoader.LoadFromDisk(path);
                List<BenchmarkEntry> entries = PixelSumBenchmarkRunner.BenchmarkSingleImageAllStrategies(img);
                allEntries.AddRange(entries);
            }

            string[] batchPaths = Directory.GetFiles(imgDir, "batch_*.png");
            Array.Sort(batchPaths, StringComparer.OrdinalIgnoreCase);
            if (batchPaths.Length > 0)
            {
                var loadedBatch = new List<LoadedDiskImage>();
                try
                {
                    foreach (string p in batchPaths) loadedBatch.Add(ImageDiskLoader.LoadFromDisk(p));
                    allEntries.AddRange(PixelSumBenchmarkRunner.BenchmarkMultiImageBatch(loadedBatch));
                }
                finally
                {
                    foreach (var img in loadedBatch) img.Dispose();
                }
            }

            // Also run a chunk-size tuning sweep on 4096x4096 for engineering optimization commentary
            string img4kPath = Path.Combine(imgDir, "test_4096x4096.png");
            if (File.Exists(img4kPath))
            {
                using var img4k = ImageDiskLoader.LoadFromDisk(img4kPath);
                int[] sweepChunks = { 4096, 16384, 65536, 262144, 1048576 };
                var sweepLog = new System.Text.StringBuilder();
                sweepLog.AppendLine("[ParallelPixelAnalysis] 4096x4096 Chunk-Size Tuning Sweep (ParallelBurstRed):");
                foreach (int cs in sweepChunks)
                {
                    var res = PixelSumBenchmarkRunner.BenchmarkSingleImageAllStrategies(img4k, 3, 15, overrideChunkSize: cs);
                    var burstRed = res.First(e => e.strategy == AnalysisStrategy.ParallelBurstRed.ToString());
                    sweepLog.AppendLine($"  ChunkSize={cs,8:N0} px ({(img4k.PixelCount + cs - 1) / cs,4} chunks) -> Median: {burstRed.medianMs:F4} ms | {burstRed.throughputMPixPerSec:N1} MPix/s | Valid={burstRed.matchesReference}");
                }
                Debug.Log(sweepLog.ToString());
            }

            BenchmarkReport report = PixelSumBenchmarkRunner.CreateReport(allEntries);
            TelemetryExporter.LogSummaryToConsole(report);

            string exportDir = TelemetryExporter.GetDefaultExportDirectory();
            Directory.CreateDirectory(exportDir);
            TelemetryExporter.WriteCsv(report, Path.Combine(exportDir, "editor_telemetry_results.csv"));
            TelemetryExporter.WriteJson(report, Path.Combine(exportDir, "editor_telemetry_results.json"));

            bool allValid = allEntries.All(e => e.matchesReference);
            Debug.Log($"[ParallelAnalysisEditorTools] Editor benchmark complete. Records={allEntries.Count}, AllBitExactMatch={allValid}");
            if (!allValid)
            {
                throw new Exception("One or more parallel benchmark entries failed bit-exactness verification!");
            }
        }

        [MenuItem("Tools/Parallel Pixel Analysis/3. Build Windows Standalone Player")]
        public static void BuildWindowsStandalonePlayer()
        {
            SetupSampleScene();

            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string buildDir = Path.Combine(projectRoot, "Builds", "Windows");
            Directory.CreateDirectory(buildDir);
            string exePath = Path.Combine(buildDir, "ParallelPixelAnalysis.exe");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { SampleScenePath },
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[ParallelAnalysisEditorTools] Windows build succeeded: {exePath} ({summary.totalSize / (1024f * 1024f):F1} MB in {summary.totalTime.TotalSeconds:F1}s)");
            }
            else
            {
                Debug.LogError($"[ParallelAnalysisEditorTools] Windows build failed: {summary.result} (Errors: {summary.totalErrors})");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }
            }
        }

        public static void RunSetupBenchmarkAndBuild()
        {
            SetupSampleScene();
            RunFullBenchmarkSuiteInEditor();
            BuildWindowsStandalonePlayer();
        }
    }
}
