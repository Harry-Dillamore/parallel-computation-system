#!/usr/bin/env python3
"""
Generate Matplotlib benchmark graphs from BenchmarkResults/telemetry_results.csv
and BenchmarkResults/editor_telemetry_results.csv.

Outputs high-resolution PNG charts into Media/Graphs/.
"""

import csv
from pathlib import Path
import numpy as np
import matplotlib.pyplot as plt
import matplotlib.ticker as ticker

REPO_ROOT = Path(__file__).resolve().parent.parent
STANDALONE_CSV = REPO_ROOT / "BenchmarkResults" / "telemetry_results.csv"
EDITOR_CSV = REPO_ROOT / "BenchmarkResults" / "editor_telemetry_results.csv"
OUT_DIR = REPO_ROOT / "Media" / "Graphs"
OUT_DIR.mkdir(parents=True, exist_ok=True)

# Clean modern dark-slate styling with crisp contrast
plt.rcParams.update({
    "figure.facecolor": "#0f172a",
    "axes.facecolor": "#1e293b",
    "axes.edgecolor": "#334155",
    "axes.labelcolor": "#e2e8f0",
    "axes.titlecolor": "#f8fafc",
    "xtick.color": "#cbd5e1",
    "ytick.color": "#cbd5e1",
    "text.color": "#f8fafc",
    "grid.color": "#334155",
    "grid.linestyle": "--",
    "grid.alpha": 0.6,
    "legend.facecolor": "#0f172a",
    "legend.edgecolor": "#334155",
    "font.sans-serif": ["DejaVu Sans"],
    "font.size": 10,
})

STRATEGIES = [
    ("SingleThreadManaged", "Single-Thread C#", "#f87171"),
    ("SingleThreadBurst", "Single-Thread Burst", "#38bdf8"),
    ("ParallelNoBurst", "Parallel (No Burst)", "#fb923c"),
    ("ParallelBurstRed", "Parallel + Burst (Red)", "#34d399"),
    ("ParallelBurstRgba", "Parallel + Burst (RGBA)", "#a78bfa"),
]

RESOLUTIONS = [
    ("test_512x512.png", "512²\n(1 MB)"),
    ("test_1024x1024.png", "1024²\n(4 MB)"),
    ("test_2048x2048.png", "2048²\n(16 MB)"),
    ("test_4096x4096.png", "4096²\n(64 MB)"),
]


def load_csv(path: Path):
    rows = {}
    with path.open(newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            rows[(r["ImageName"], r["Strategy"])] = {
                "pixelCount": int(r["PixelCount"]),
                "diskLoadMs": float(r["DiskLoadMs"]),
                "minMs": float(r["MinMs"]),
                "medianMs": float(r["MedianMs"]),
                "meanMs": float(r["MeanMs"]),
                "maxMs": float(r["MaxMs"]),
                "stdDevMs": float(r["StdDevMs"]),
                "throughputMPix": float(r["ThroughputMPixPerSec"]),
                "speedup": float(r["SpeedupVsManaged"]),
            }
    return rows


def plot_execution_time_and_speedup(standalone):
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(15, 6), dpi=200)
    fig.suptitle(
        "Parallel Pixel Computation — Standalone Burst AOT Performance (AMD Ryzen 7 260, 15 Workers)",
        fontsize=14,
        fontweight="bold",
        y=0.97,
    )

    x = np.arange(len(RESOLUTIONS))
    width = 0.16

    # Left: Median Execution Time (log scale)
    for idx, (strat, label, color) in enumerate(STRATEGIES):
        vals = [standalone[(img, strat)]["medianMs"] for img, _ in RESOLUTIONS]
        offset = (idx - 2) * width
        bars = ax1.bar(x + offset, vals, width, label=label, color=color, edgecolor="#0f172a", linewidth=0.6)
        if strat in ("SingleThreadManaged", "ParallelBurstRed"):
            for bar, v in zip(bars, vals):
                txt = f"{v * 1000:.1f}µs" if v < 0.05 else f"{v:.2f}"
                ax1.text(
                    bar.get_x() + bar.get_width() / 2,
                    v * 1.22,
                    txt,
                    ha="center",
                    va="bottom",
                    fontsize=7.2,
                    color=color,
                    fontweight="bold",
                )

    ax1.set_yscale("log")
    ax1.set_ylim(0.004, 25)
    ax1.set_xticks(x)
    ax1.set_xticklabels([lbl for _, lbl in RESOLUTIONS])
    ax1.set_ylabel("Median Execution Time (ms, log scale)")
    ax1.set_title("A. Execution Time by Strategy (Lower is Better)", fontweight="bold", pad=10)
    ax1.yaxis.set_major_formatter(ticker.FuncFormatter(lambda v, _: f"{v:g} ms"))
    ax1.grid(axis="y", which="major")
    ax1.set_axisbelow(True)

    # Right: Speedup vs Managed C# (include 4x 2048 batch)
    x_labels = [lbl for _, lbl in RESOLUTIONS] + ["4× 2048²\nBatch (64 MB)"]
    x2 = np.arange(len(x_labels))
    speed_strats = STRATEGIES[1:]  # exclude baseline 1.0x
    w2 = 0.19

    for idx, (strat, label, color) in enumerate(speed_strats):
        vals = []
        for img, _ in RESOLUTIONS:
            vals.append(standalone[(img, strat)]["speedup"])
        if strat == "ParallelBurstRed":
            vals.append(standalone[("Batch_4x_2048x2048", "MultiImageConcurrentBurst")]["speedup"])
        else:
            vals.append(0.0)

        offset = (idx - 1.5) * w2
        bars = ax2.bar(x2 + offset, vals, w2, label=label, color=color, edgecolor="#0f172a", linewidth=0.6)
        if strat == "ParallelBurstRed":
            for bar, v in zip(bars, vals):
                if v > 0:
                    ax2.text(
                        bar.get_x() + bar.get_width() / 2,
                        v + 1.0,
                        f"{v:.2f}×",
                        ha="center",
                        va="bottom",
                        fontsize=8.5,
                        color="#34d399",
                        fontweight="bold",
                    )

    ax2.axhline(1.0, color="#f87171", linestyle=":", linewidth=1.2, label="Managed C# Baseline (1.0×)")
    ax2.set_ylim(0, 58)
    ax2.set_xticks(x2)
    ax2.set_xticklabels(x_labels)
    ax2.set_ylabel("Speedup vs. Single-Thread C# (× faster)")
    ax2.set_title("B. Speedup Scaling Across Resolutions (Higher is Better)", fontweight="bold", pad=10)
    ax2.grid(axis="y")
    ax2.set_axisbelow(True)

    # Annotate L3 Peak
    ax2.annotate(
        "Peak inside 16 MB L3 Cache",
        xy=(1 + 0.5 * w2, 49.46),
        xytext=(2.15, 48.5),
        arrowprops=dict(arrowstyle="->", color="#34d399", lw=1.3),
        fontsize=9,
        fontweight="bold",
        color="#34d399",
    )

    handles, labels = ax1.get_legend_handles_labels()
    fig.legend(handles, labels, loc="upper center", bbox_to_anchor=(0.5, 0.915), ncol=5, frameon=True, fontsize=9)
    plt.tight_layout(rect=[0, 0, 1, 0.87])
    out = OUT_DIR / "01_execution_time_and_speedup.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def plot_cache_to_dram_roofline(standalone):
    fig, ax = plt.subplots(figsize=(12, 5.8), dpi=200)

    items = [
        ("512² (1 MB)\nL2/L3 Resident", standalone[("test_512x512.png", "ParallelBurstRed")], "#34d399"),
        ("1024² (4 MB)\nL3 Cache Peak", standalone[("test_1024x1024.png", "ParallelBurstRed")], "#10b981"),
        ("2048² (16 MB)\nL3 Boundary", standalone[("test_2048x2048.png", "ParallelBurstRed")], "#38bdf8"),
        ("4096² (64 MB)\nBurst Red", standalone[("test_4096x4096.png", "ParallelBurstRed")], "#fb923c"),
        ("4096² (64 MB)\nBurst RGBA", standalone[("test_4096x4096.png", "ParallelBurstRgba")], "#a78bfa"),
        ("4× 2048² (64 MB)\nConcurrent Burst", standalone[("Batch_4x_2048x2048", "MultiImageConcurrentBurst")], "#facc15"),
        ("4× 2048² (64 MB)\nSequential C#", standalone[("Batch_4x_2048x2048", "MultiImageSequentialManaged")], "#f87171"),
    ]

    labels = [it[0] for it in items]
    gpix = [it[1]["throughputMPix"] / 1000.0 for it in items]
    gbs = [g * 4.0 for g in gpix]  # 4 bytes per Color32 pixel
    colors = [it[2] for it in items]

    x = np.arange(len(items))
    bars = ax.bar(x, gpix, width=0.58, color=colors, edgecolor="#0f172a", linewidth=0.8)

    # Shaded regions
    ax.axvspan(-0.5, 2.5, color="#10b981", alpha=0.07, label="Cache-Resident Regime (≤ 16 MB L3)")
    ax.axvspan(2.5, 6.5, color="#fb923c", alpha=0.07, label="DRAM-Bound Regime (> 16 MB L3)")
    ax.axhline(10.35, color="#fb923c", linestyle="--", linewidth=1.4, label="System DRAM Ceiling (~41–42 GB/s)")

    for bar, g, bw in zip(bars, gpix, gbs):
        y_pos = g + 2.2 if g >= 8.0 else 12.2
        ax.text(
            bar.get_x() + bar.get_width() / 2,
            y_pos,
            f"{g:.1f} GPix/s\n({bw:.0f} GB/s)",
            ha="center",
            va="bottom",
            fontsize=8.5,
            fontweight="bold",
            color="#f8fafc",
        )

    ax.annotate(
        "RGBA (1.59 ms) = Red-only (1.64 ms)\nProves 4K is memory-bandwidth bound!",
        xy=(4.0, gpix[4] + 15.5),
        xytext=(3.55, 58),
        arrowprops=dict(arrowstyle="->", color="#a78bfa", lw=1.5),
        fontsize=9,
        fontweight="bold",
        color="#e9d5ff",
        bbox=dict(boxstyle="round,pad=0.4", facecolor="#1e1b4b", edgecolor="#a78bfa", lw=1),
    )

    ax.set_ylim(0, 130)
    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=9)
    ax.set_ylabel("Throughput (GPix/s — 4 Bytes/Pixel)")
    ax.set_title(
        "Cache-to-DRAM Roofline Transition: Throughput & Effective Memory Bandwidth",
        fontsize=13,
        fontweight="bold",
        pad=12,
    )
    ax.grid(axis="y")
    ax.set_axisbelow(True)
    ax.legend(loc="upper right", frameon=True, fontsize=9)

    plt.tight_layout()
    out = OUT_DIR / "02_cache_to_dram_roofline.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def plot_standalone_vs_editor(standalone, editor):
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(14, 5.5), dpi=200)
    fig.suptitle(
        "Unity Editor (Burst Safety Checks) vs. Windows Standalone Burst AOT",
        fontsize=14,
        fontweight="bold",
        y=0.97,
    )

    x = np.arange(len(RESOLUTIONS))
    w = 0.34

    # Left: ParallelBurstRed Speedup comparison
    s_red = [standalone[(img, "ParallelBurstRed")]["speedup"] for img, _ in RESOLUTIONS]
    e_red = [editor[(img, "ParallelBurstRed")]["speedup"] for img, _ in RESOLUTIONS]

    b1 = ax1.bar(x - w / 2, s_red, w, label="Standalone Burst AOT", color="#34d399", edgecolor="#0f172a")
    b2 = ax1.bar(x + w / 2, e_red, w, label="Unity Editor (Safety Checks)", color="#f87171", edgecolor="#0f172a")

    for bar, v in zip(b1, s_red):
        ax1.text(bar.get_x() + bar.get_width() / 2, v + 0.9, f"{v:.2f}×", ha="center", va="bottom", fontsize=8.5, fontweight="bold", color="#34d399")
    for bar, v in zip(b2, e_red):
        ax1.text(bar.get_x() + bar.get_width() / 2, v + 0.9, f"{v:.2f}×", ha="center", va="bottom", fontsize=8.5, fontweight="bold", color="#f87171")

    ax1.set_ylim(0, 58)
    ax1.set_xticks(x)
    ax1.set_xticklabels([lbl for _, lbl in RESOLUTIONS])
    ax1.set_ylabel("Speedup vs. Single-Thread C# (× faster)")
    ax1.set_title("A. Parallel + Burst (Red) Speedup", fontweight="bold", pad=10)
    ax1.grid(axis="y")
    ax1.set_axisbelow(True)
    ax1.legend(frameon=True)

    # Right: SingleThreadBurst Speedup comparison (shows Editor safety check penalty vividly!)
    s_1b = [standalone[(img, "SingleThreadBurst")]["speedup"] for img, _ in RESOLUTIONS]
    e_1b = [editor[(img, "SingleThreadBurst")]["speedup"] for img, _ in RESOLUTIONS]

    b3 = ax2.bar(x - w / 2, s_1b, w, label="Standalone Burst AOT", color="#38bdf8", edgecolor="#0f172a")
    b4 = ax2.bar(x + w / 2, e_1b, w, label="Unity Editor (Safety Checks)", color="#fb923c", edgecolor="#0f172a")

    for bar, v in zip(b3, s_1b):
        ax2.text(bar.get_x() + bar.get_width() / 2, v + 0.25, f"{v:.2f}×", ha="center", va="bottom", fontsize=8.5, fontweight="bold", color="#38bdf8")
    for bar, v in zip(b4, e_1b):
        ax2.text(bar.get_x() + bar.get_width() / 2, max(v + 0.25, 1.18), f"{v:.2f}×", ha="center", va="bottom", fontsize=8.5, fontweight="bold", color="#fb923c")

    ax2.axhline(1.0, color="#cbd5e1", linestyle=":", linewidth=1.2, label="1.0× Managed C# Baseline")
    ax2.set_ylim(0, 11.5)
    ax2.set_xticks(x)
    ax2.set_xticklabels([lbl for _, lbl in RESOLUTIONS])
    ax2.set_ylabel("Speedup vs. Single-Thread C# (× faster)")
    ax2.set_title("B. Single-Thread Burst Speedup (Isolating SIMD)", fontweight="bold", pad=10)
    ax2.grid(axis="y")
    ax2.set_axisbelow(True)
    ax2.legend(frameon=True)

    plt.tight_layout(rect=[0, 0, 1, 0.93])
    out = OUT_DIR / "03_standalone_aot_vs_editor.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def plot_disk_decode_vs_compute(standalone):
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(13.5, 5.2), dpi=200, gridspec_kw={"width_ratios": [1.3, 1.0]})
    fig.suptitle(
        "4K (4096×4096) End-to-End Bottleneck: Disk PNG Decode vs. Pixel Computation",
        fontsize=13.5,
        fontweight="bold",
        y=0.97,
    )

    r4k_managed = standalone[("test_4096x4096.png", "SingleThreadManaged")]
    r4k_1burst = standalone[("test_4096x4096.png", "SingleThreadBurst")]
    r4k_pnb = standalone[("test_4096x4096.png", "ParallelNoBurst")]
    r4k_pred = standalone[("test_4096x4096.png", "ParallelBurstRed")]
    r4k_prgba = standalone[("test_4096x4096.png", "ParallelBurstRgba")]

    # Left: Full End-to-End Scale (shows PNG Decode completely dominates!)
    labels_all = [
        "PNG Disk Read\n+ CPU Decode",
        "Single-Thread\nManaged C#",
        "Parallel + Burst\n(Red)",
        "Parallel + Burst\n(RGBA)",
    ]
    vals_all = [r4k_managed["diskLoadMs"], r4k_managed["medianMs"], r4k_pred["medianMs"], r4k_prgba["medianMs"]]
    cols_all = ["#ef4444", "#f87171", "#34d399", "#a78bfa"]

    y1 = np.arange(len(labels_all))
    bars1 = ax1.barh(y1, vals_all, color=cols_all, height=0.55, edgecolor="#0f172a")
    ax1.invert_yaxis()
    ax1.set_xlim(0, 195)
    ax1.set_yticks(y1)
    ax1.set_yticklabels(labels_all, fontsize=9.5)
    ax1.set_xlabel("Time (ms — Lower is Better)")
    ax1.set_title("A. Full End-to-End Pipeline Time (4096²)", fontweight="bold", pad=10)
    ax1.grid(axis="x")
    ax1.set_axisbelow(True)

    ratio = r4k_managed["diskLoadMs"] / r4k_pred["medianMs"]
    for bar, v in zip(bars1, vals_all):
        ax1.text(v + 2.5, bar.get_y() + bar.get_height() / 2, f"{v:.2f} ms", va="center", fontsize=9.5, fontweight="bold")

    ax1.text(
        82,
        2.3,
        f"PNG decode ({r4k_managed['diskLoadMs']:.1f} ms) is {ratio:.0f}× slower\nthan Burst compute ({r4k_pred['medianMs']:.2f} ms)\n(~99% of total time!)",
        fontsize=9.5,
        fontweight="bold",
        color="#fecaca",
        bbox=dict(boxstyle="round,pad=0.5", facecolor="#450a0a", edgecolor="#ef4444", lw=1.2),
    )

    # Right: Zoomed-in Compute-Only Comparison at 4K
    labels_cmp = [
        "Single-Thread C#",
        "Single-Thread Burst",
        "Parallel (No Burst)",
        "Parallel + Burst (Red)",
        "Parallel + Burst (RGBA)",
    ]
    vals_cmp = [r4k_managed["medianMs"], r4k_1burst["medianMs"], r4k_pnb["medianMs"], r4k_pred["medianMs"], r4k_prgba["medianMs"]]
    cols_cmp = ["#f87171", "#38bdf8", "#fb923c", "#34d399", "#a78bfa"]

    y2 = np.arange(len(labels_cmp))
    bars2 = ax2.barh(y2, vals_cmp, color=cols_cmp, height=0.55, edgecolor="#0f172a")
    ax2.invert_yaxis()
    ax2.set_xlim(0, 9.6)
    ax2.set_yticks(y2)
    ax2.set_yticklabels(labels_cmp, fontsize=9)
    ax2.set_xlabel("Median Compute Time (ms)")
    ax2.set_title("B. Zoomed: 4096² Compute Only", fontweight="bold", pad=10)
    ax2.grid(axis="x")
    ax2.set_axisbelow(True)

    for bar, v in zip(bars2, vals_cmp):
        ax2.text(v + 0.15, bar.get_y() + bar.get_height() / 2, f"{v:.2f} ms", va="center", fontsize=9, fontweight="bold")

    plt.tight_layout(rect=[0, 0, 1, 0.92])
    out = OUT_DIR / "04_disk_decode_vs_compute.png"
    fig.savefig(out)
    plt.close(fig)
    return out


def main():
    standalone = load_csv(STANDALONE_CSV)
    editor = load_csv(EDITOR_CSV)

    outputs = [
        plot_execution_time_and_speedup(standalone),
        plot_cache_to_dram_roofline(standalone),
        plot_standalone_vs_editor(standalone, editor),
        plot_disk_decode_vs_compute(standalone),
    ]
    for p in outputs:
        print(f"Generated: {p}")


if __name__ == "__main__":
    main()
