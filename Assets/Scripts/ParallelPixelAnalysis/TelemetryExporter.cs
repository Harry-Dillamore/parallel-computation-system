using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ParallelPixelAnalysis
{
    /// <summary>
    /// Exports benchmark telemetry to CSV and JSON files and emits formatted Unity console summaries.
    /// </summary>
    public static class TelemetryExporter
    {
        public static string GetDefaultExportDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--export-dir", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            if (Application.isEditor)
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "BenchmarkResults");
            }

            string buildParent = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(buildParent, "BenchmarkResults");
        }

        public static (string csvPath, string jsonPath) ExportAll(BenchmarkReport report, string outputDir = null)
        {
            string dir = string.IsNullOrWhiteSpace(outputDir) ? GetDefaultExportDirectory() : outputDir;
            Directory.CreateDirectory(dir);

            string csvPath = Path.Combine(dir, "telemetry_results.csv");
            string jsonPath = Path.Combine(dir, "telemetry_results.json");

            WriteCsv(report, csvPath);
            WriteJson(report, jsonPath);

            Debug.Log($"[ParallelPixelAnalysis] Exported telemetry:\n  CSV : {csvPath}\n  JSON: {jsonPath}");
            return (csvPath, jsonPath);
        }

        public static void WriteJson(BenchmarkReport report, string filePath)
        {
            string json = JsonUtility.ToJson(report, prettyPrint: true);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        public static void WriteCsv(BenchmarkReport report, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "TimestampUtc,RuntimeEnvironment,Processor,LogicalCores,JobWorkers,BurstEnabled," +
                "ImageName,Width,Height,PixelCount,TextureFormat,DiskLoadMs," +
                "Strategy,ChunkSize,BatchCount,WarmupRuns,MeasuredRuns," +
                "MinMs,MedianMs,MeanMs,MaxMs,StdDevMs,ThroughputMPixPerSec,SpeedupVsManaged," +
                "RedSum,GreenSum,BlueSum,AlphaSum,MatchesReference");

            var inv = CultureInfo.InvariantCulture;
            string safeCpu = EscapeCsv(report.processorType);
            string safeEnv = EscapeCsv(report.runtimeEnvironment);

            foreach (BenchmarkEntry e in report.entries)
            {
                sb.Append(EscapeCsv(e.timestampUtc)).Append(',')
                  .Append(safeEnv).Append(',')
                  .Append(safeCpu).Append(',')
                  .Append(report.logicalProcessorCount.ToString(inv)).Append(',')
                  .Append(report.jobWorkerCount.ToString(inv)).Append(',')
                  .Append(report.burstEnabled ? "true" : "false").Append(',')
                  .Append(EscapeCsv(e.imageName)).Append(',')
                  .Append(e.width.ToString(inv)).Append(',')
                  .Append(e.height.ToString(inv)).Append(',')
                  .Append(e.pixelCount.ToString(inv)).Append(',')
                  .Append(EscapeCsv(e.textureFormat)).Append(',')
                  .Append(e.diskLoadMs.ToString("F4", inv)).Append(',')
                  .Append(EscapeCsv(e.strategy)).Append(',')
                  .Append(e.chunkSize.ToString(inv)).Append(',')
                  .Append(e.innerLoopBatchCount.ToString(inv)).Append(',')
                  .Append(e.warmupIterations.ToString(inv)).Append(',')
                  .Append(e.measuredIterations.ToString(inv)).Append(',')
                  .Append(e.minMs.ToString("F5", inv)).Append(',')
                  .Append(e.medianMs.ToString("F5", inv)).Append(',')
                  .Append(e.meanMs.ToString("F5", inv)).Append(',')
                  .Append(e.maxMs.ToString("F5", inv)).Append(',')
                  .Append(e.stdDevMs.ToString("F5", inv)).Append(',')
                  .Append(e.throughputMPixPerSec.ToString("F2", inv)).Append(',')
                  .Append(e.speedupVsManaged.ToString("F2", inv)).Append(',')
                  .Append(e.redSum.ToString(inv)).Append(',')
                  .Append(e.greenSum.ToString(inv)).Append(',')
                  .Append(e.blueSum.ToString(inv)).Append(',')
                  .Append(e.alphaSum.ToString(inv)).Append(',')
                  .Append(e.matchesReference ? "true" : "false")
                  .AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        public static string FormatConsoleSummary(BenchmarkReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=====================================================================================================");
            sb.AppendLine($"[ParallelPixelAnalysis] Benchmark Summary | {report.runtimeEnvironment} | Unity {report.unityVersion}");
            sb.AppendLine($"CPU: {report.processorType} ({report.logicalProcessorCount} logical cores, {report.jobWorkerCount} Job Workers, Burst={report.burstEnabled})");
            sb.AppendLine("-----------------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-24} {1,-27} {2,10} {3,10} {4,11} {5,9} {6,15} {7,6}",
                "Image", "Strategy", "Median(ms)", "Mean(ms)", "MPix/sec", "Speedup", "Red Sum", "Valid"));
            sb.AppendLine("-----------------------------------------------------------------------------------------------------");

            foreach (BenchmarkEntry e in report.entries)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-24} {1,-27} {2,10:F4} {3,10:F4} {4,11:N1} {5,8:F2}x {6,15:N0} {7,6}",
                    Truncate(e.imageName, 24),
                    Truncate(e.strategy, 27),
                    e.medianMs,
                    e.meanMs,
                    e.throughputMPixPerSec,
                    e.speedupVsManaged,
                    e.redSum,
                    e.matchesReference ? "OK" : "FAIL"));
            }

            sb.AppendLine("=====================================================================================================");
            return sb.ToString();
        }

        public static void LogSummaryToConsole(BenchmarkReport report)
        {
            Debug.Log(FormatConsoleSummary(report));
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s ?? "";
            return s.Substring(0, maxLen - 1) + "~";
        }
    }
}
