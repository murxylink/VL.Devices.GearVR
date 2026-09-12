using System.Diagnostics;
using System.Globalization;
using VL.Core.Import;
using Vector2 = Stride.Core.Mathematics.Vector2;
using Vector3 = Stride.Core.Mathematics.Vector3;

namespace VL.Devices.GearVR;

/// <summary>
/// Replays a CSV produced by Sensor Recorder and outputs a controller handle compatible with
/// Clicks, Movement, Touchpad, and Sensor Recorder.
/// </summary>
[ProcessNode(Name = "Sensor Player", Category = "Devices.GearVR")]
public sealed class SensorPlayer
{
    private readonly GearVRController _controller = new(isPlaybackController: true);
    private readonly List<PlaybackFrame> _frames = new();
    private string _loadedFilePath = "";
    private string _error = "";
    private int _frameIndex;
    private double _positionSeconds;
    private long _lastTicks;
    private bool _previousPlay;
    private bool _previousRestart;
    private bool _finished;

    /// <summary>
    /// Loads a Sensor Recorder CSV. Play uses the recording's original report timing; Speed
    /// scales wall-clock playback, Loop repeats the recording, and Restart returns to its first frame.
    /// </summary>
    public void Update(
        out GearVRController controller,
        out GearVRControllerInfo info,
        out bool isPlaying,
        out float positionSeconds,
        out float durationSeconds,
        out float progress,
        out int frame,
        out int frameCount,
        out string error,
        string filePath = "",
        bool play = false,
        float speed = 1f,
        bool loop = false,
        bool restart = false)
    {
        if (!string.Equals(filePath, _loadedFilePath, StringComparison.Ordinal))
            Load(filePath);
        if (restart && !_previousRestart)
            Restart();
        _previousRestart = restart;

        if (play && !_previousPlay)
        {
            if (_finished)
                Restart();
            _lastTicks = Stopwatch.GetTimestamp();
        }
        _previousPlay = play;

        var duration = DurationSeconds;
        if (play && !_finished && _frames.Count > 0)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastTicks != 0)
            {
                var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _positionSeconds += elapsed * Math.Clamp(speed, 0f, 16f);
            }
            _lastTicks = now;

            if (duration <= 0 || _positionSeconds >= duration)
            {
                if (loop && duration > 0)
                {
                    _positionSeconds %= duration;
                    _frameIndex = 0;
                }
                else
                {
                    _positionSeconds = duration;
                    _finished = true;
                }
            }
            AdvanceFrame();
        }
        else
        {
            _lastTicks = 0;
        }

        ApplyCurrentFrame();
        controller = _controller;
        info = _controller.GetSnapshot();
        isPlaying = play && !_finished && _frames.Count > 0;
        positionSeconds = (float)_positionSeconds;
        durationSeconds = (float)duration;
        progress = duration > 0 ? Math.Clamp((float)(_positionSeconds / duration), 0f, 1f) : 0f;
        frame = _frames.Count == 0 ? 0 : _frameIndex;
        frameCount = _frames.Count;
        error = _error;
    }

    private double DurationSeconds => _frames.Count > 1 ? _frames[^1].TimeSeconds : 0;

    private void Load(string filePath)
    {
        _frames.Clear();
        _loadedFilePath = filePath;
        _error = "";
        Restart();
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            using var reader = new StreamReader(filePath);
            var header = reader.ReadLine() ?? throw new InvalidDataException("The CSV file has no header.");
            var columns = ParseCsv(header)
                .Select((name, index) => (name, index))
                .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
            ValidateColumns(columns);

            DateTimeOffset? firstTime = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                    continue;
                var fields = ParseCsv(line);
                var timestamp = DateTimeOffset.Parse(Get(fields, columns, "utc_time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                firstTime ??= timestamp;
                _frames.Add(new PlaybackFrame(timestamp, (timestamp - firstTime.Value).TotalSeconds, CreateInfo(fields, columns)));
            }

            if (_frames.Count == 0)
                throw new InvalidDataException("The CSV file does not contain sensor samples.");
            Restart();
        }
        catch (Exception exception)
        {
            _frames.Clear();
            _error = exception.Message;
        }
    }

    private void Restart()
    {
        _positionSeconds = 0;
        _frameIndex = 0;
        _lastTicks = 0;
        _finished = false;
        ApplyCurrentFrame();
    }

    private void AdvanceFrame()
    {
        while (_frameIndex < _frames.Count - 1 && _frames[_frameIndex + 1].TimeSeconds <= _positionSeconds)
            _frameIndex++;
    }

    private void ApplyCurrentFrame()
    {
        if (_frames.Count == 0)
        {
            _controller.SetPlaybackSnapshot(GearVRControllerInfo.Searching(error: _error));
            return;
        }

        var source = _frames[_frameIndex].Info;
        _controller.SetPlaybackSnapshot(source with
        {
            ConnectionState = GearVRConnectionState.Connected,
            Name = $"Sensor Player ({Path.GetFileName(_loadedFilePath)})",
            DeviceId = $"playback:{Path.GetFullPath(_loadedFilePath)}",
            Error = "",
            AgeSeconds = 0
        });
    }

    private static GearVRControllerInfo CreateInfo(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns)
    {
        var rawReportText = Get(fields, columns, "raw_report_hex");
        var rawReport = rawReportText.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(rawReportText);
        var touchX = Int(fields, columns, "touch_x");
        var touchY = Int(fields, columns, "touch_y");
        return new GearVRControllerInfo(
            GearVRConnectionState.Connected, "Sensor Player", "playback", "", Long(fields, columns, "sequence"),
            Long(fields, columns, "device_timestamp"), 0, Int(fields, columns, "battery_percent"), Int(fields, columns, "temperature_c"),
            Vector(fields, columns, "raw_accel"), Vector(fields, columns, "accel_mps2"),
            Vector(fields, columns, "raw_gyro"), Vector(fields, columns, "gyro_rads"),
            Vector(fields, columns, "raw_mag"), Vector(fields, columns, "mag_ut"),
            touchX, touchY, new Vector2(Float(fields, columns, "touch_normalized_x"), Float(fields, columns, "touch_normalized_y")),
            Bool(fields, columns, "is_touched"), Bool(fields, columns, "trigger"), Bool(fields, columns, "home"),
            Bool(fields, columns, "back"), Bool(fields, columns, "touchpad_click"), Bool(fields, columns, "volume_up"),
            Bool(fields, columns, "volume_down"), Bool(fields, columns, "no_button"), rawReport);
    }

    private static Vector3 Vector(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string prefix) =>
        new(Float(fields, columns, $"{prefix}_x"), Float(fields, columns, $"{prefix}_y"), Float(fields, columns, $"{prefix}_z"));

    private static float Float(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name) =>
        float.Parse(Get(fields, columns, name), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int Int(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name) =>
        int.Parse(Get(fields, columns, name), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static long Long(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name) =>
        long.Parse(Get(fields, columns, name), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static bool Bool(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name) =>
        Get(fields, columns, name) is "1" or "true" or "True";

    private static string Get(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columns, string name) =>
        columns.TryGetValue(name, out var index) && index < fields.Count
            ? fields[index]
            : throw new InvalidDataException($"The CSV file is missing '{name}'.");

    private static void ValidateColumns(IReadOnlyDictionary<string, int> columns)
    {
        var required = new[]
        {
            "utc_time", "sequence", "device_timestamp", "battery_percent", "temperature_c", "raw_report_hex",
            "raw_accel_x", "raw_accel_y", "raw_accel_z", "accel_mps2_x", "accel_mps2_y", "accel_mps2_z",
            "raw_gyro_x", "raw_gyro_y", "raw_gyro_z", "gyro_rads_x", "gyro_rads_y", "gyro_rads_z",
            "raw_mag_x", "raw_mag_y", "raw_mag_z", "mag_ut_x", "mag_ut_y", "mag_ut_z",
            "touch_x", "touch_y", "touch_normalized_x", "touch_normalized_y", "is_touched", "trigger", "home",
            "back", "touchpad_click", "volume_up", "volume_down", "no_button"
        };
        var missing = required.Where(name => !columns.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Not a Sensor Recorder CSV. Missing: {string.Join(", ", missing)}.");
    }

    private static List<string> ParseCsv(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append(character);
                    index++;
                }
                else
                    quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
                value.Append(character);
        }
        values.Add(value.ToString());
        return values;
    }

    private sealed record PlaybackFrame(DateTimeOffset Timestamp, double TimeSeconds, GearVRControllerInfo Info);
}
