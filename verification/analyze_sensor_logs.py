"""Read-only summary for Gear VR Sensor Recorder CSV exports."""

import csv
import math
import statistics
import sys
from datetime import datetime
from pathlib import Path

AXES = ("x", "y", "z")


def values(rows, prefix):
    return [[float(row[f"{prefix}_{axis}"]) for axis in AXES] for row in rows]


def report_values(rows, offset):
    result = []
    for row in rows:
        report = bytes.fromhex(row["raw_report_hex"])
        result.append([int.from_bytes(report[offset + index : offset + index + 2], "little", signed=True) for index in (0, 2, 4)])
    return result


def norms(vectors):
    return [math.sqrt(sum(component * component for component in vector)) for vector in vectors]


def stats(numbers):
    return {
        "mean": statistics.fmean(numbers),
        "stdev": statistics.stdev(numbers) if len(numbers) > 1 else 0.0,
        "min": min(numbers),
        "max": max(numbers),
    }


def render_stats(label, vectors, unit):
    components = [stats([row[index] for row in vectors]) for index in range(3)]
    magnitude = stats(norms(vectors))
    component_text = "; ".join(
        f"{axis}: {item['mean']:.5g} +/- {item['stdev']:.4g} [{item['min']:.5g}, {item['max']:.5g}]"
        for axis, item in zip(AXES, components)
    )
    print(f"  {label} ({unit})\n    {component_text}\n    norm: {magnitude['mean']:.5g} +/- {magnitude['stdev']:.4g} [{magnitude['min']:.5g}, {magnitude['max']:.5g}]")


def analyze(path):
    with path.open(encoding="utf-8-sig", newline="") as source:
        rows = list(csv.DictReader(source))
    timestamps = [datetime.fromisoformat(row["utc_time"]) for row in rows]
    intervals = [(right - left).total_seconds() for left, right in zip(timestamps, timestamps[1:])]
    duration = (timestamps[-1] - timestamps[0]).total_seconds() if len(rows) > 1 else 0.0
    print(f"\n{path.name}\n  samples: {len(rows)}, duration: {duration:.3f} s, average rate: {(len(rows)-1)/duration if duration else 0:.2f} Hz")
    if intervals:
        print(f"  report interval: mean {statistics.fmean(intervals)*1000:.3f} ms, stdev {statistics.stdev(intervals)*1000:.3f} ms, max {max(intervals)*1000:.3f} ms")
    render_stats("acceleration", values(rows, "accel_mps2"), "m/s^2")
    render_stats("gyroscope", values(rows, "gyro_rads"), "rad/s")
    render_stats("magnetic field", values(rows, "mag_ut"), "uT")
    render_stats("raw acceleration", values(rows, "raw_accel"), "counts")
    render_stats("raw gyroscope", values(rows, "raw_gyro"), "counts")
    render_stats("raw magnetometer", values(rows, "raw_mag"), "counts")
    render_stats("report tail candidate", report_values(rows, 48), "counts")
    battery = sorted(set(row["battery_percent"] for row in rows))
    temperatures = sorted(set(row["temperature_c"] for row in rows))
    print(f"  battery values: {', '.join(battery)}; temperature values: {', '.join(temperatures)}")
    if duration >= 10:
        tail_start = timestamps[-1].timestamp() - 10
        tail = [row for row, timestamp in zip(rows, timestamps) if timestamp.timestamp() >= tail_start]
        print(f"  final stillness window ({len(tail)} samples, last 10 s)")
        render_stats("acceleration", values(tail, "accel_mps2"), "m/s^2")
        render_stats("gyroscope", values(tail, "gyro_rads"), "rad/s")


if __name__ == "__main__":
    for raw_path in sys.argv[1:]:
        analyze(Path(raw_path))
