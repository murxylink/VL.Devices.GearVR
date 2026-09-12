using System.Globalization;
using System.Text;
using VL.Core.Import;

namespace VL.Devices.GearVR;

/// <summary>
/// Records every received Gear VR BLE report to a CSV file for sensor and orientation analysis.
/// The file contains raw counts, scaled values, controls, timing, and the complete raw packet.
/// </summary>
[ProcessNode(Name = "Sensor Recorder", Category = "Devices.GearVR")]
public sealed class SensorRecorder : IDisposable
{
    private StreamWriter? _writer;
    private long _lastSequence = -1;
    private long _samples;
    private string _filePath = "";
    private string _error = "";

    /// <summary>
    /// Set Record true to start a new CSV log. Leave File Path empty to write under
    /// Documents\GearVRRecordings. One row is written for every new controller report.
    /// </summary>
    public void Update(
        out bool recording,
        out long samples,
        out string filePath,
        out string error,
        GearVRController? controller = null,
        bool record = false,
        string requestedFilePath = "")
    {
        if (record && _writer is null)
            Start(requestedFilePath);
        else if (!record && _writer is not null)
            Stop();

        var info = controller?.GetSnapshot() ?? GearVRControllerInfo.Searching();
        if (_writer is not null && info.Sequence > 0 && info.Sequence != _lastSequence)
        {
            try
            {
                WriteSample(info);
                _lastSequence = info.Sequence;
                _samples++;
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Stop();
            }
        }

        recording = _writer is not null;
        samples = _samples;
        filePath = _filePath;
        error = _error;
    }

    private void Start(string requestedFilePath)
    {
        Stop();
        try
        {
            var path = ResolvePath(requestedFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _writer.WriteLine(Header);
            _filePath = path;
            _error = "";
            _lastSequence = -1;
            _samples = 0;
        }
        catch (Exception exception)
        {
            _writer = null;
            _filePath = "";
            _error = exception.Message;
        }
    }

    private void Stop()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private void WriteSample(GearVRControllerInfo info)
    {
        var fields = new[]
        {
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            info.Sequence.ToString(CultureInfo.InvariantCulture),
            info.DeviceTimestampMicroseconds.ToString(CultureInfo.InvariantCulture),
            info.ConnectionState.ToString(),
            info.BatteryPercent.ToString(CultureInfo.InvariantCulture),
            info.TemperatureCelsius.ToString(CultureInfo.InvariantCulture),
            Number(info.RawAcceleration.X), Number(info.RawAcceleration.Y), Number(info.RawAcceleration.Z),
            Number(info.Acceleration.X), Number(info.Acceleration.Y), Number(info.Acceleration.Z),
            Number(info.RawGyroscope.X), Number(info.RawGyroscope.Y), Number(info.RawGyroscope.Z),
            Number(info.AngularVelocity.X), Number(info.AngularVelocity.Y), Number(info.AngularVelocity.Z),
            Number(info.RawMagnetometer.X), Number(info.RawMagnetometer.Y), Number(info.RawMagnetometer.Z),
            Number(info.MagneticField.X), Number(info.MagneticField.Y), Number(info.MagneticField.Z),
            info.TouchX.ToString(CultureInfo.InvariantCulture), info.TouchY.ToString(CultureInfo.InvariantCulture),
            Number(info.TouchPosition.X), Number(info.TouchPosition.Y),
            Bool(info.IsTouched), Bool(info.Trigger), Bool(info.Home), Bool(info.Back), Bool(info.TouchpadClick),
            Bool(info.VolumeUp), Bool(info.VolumeDown), Bool(info.NoButton),
            Convert.ToHexString(info.RawReport)
        };
        _writer!.WriteLine(string.Join(',', fields));
    }

    private static string ResolvePath(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GearVRRecordings");
            return Path.Combine(folder, $"GearVR_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        }

        var fullPath = Path.GetFullPath(requestedPath);
        if (Path.GetExtension(fullPath).Length == 0)
            fullPath += ".csv";
        if (!File.Exists(fullPath))
            return fullPath;

        var directory = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        return Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "1" : "0";

    private const string Header = "utc_time,sequence,device_timestamp,connection_state,battery_percent,temperature_c,raw_accel_x,raw_accel_y,raw_accel_z,accel_mps2_x,accel_mps2_y,accel_mps2_z,raw_gyro_x,raw_gyro_y,raw_gyro_z,gyro_rads_x,gyro_rads_y,gyro_rads_z,raw_mag_x,raw_mag_y,raw_mag_z,mag_ut_x,mag_ut_y,mag_ut_z,touch_x,touch_y,touch_normalized_x,touch_normalized_y,is_touched,trigger,home,back,touchpad_click,volume_up,volume_down,no_button,raw_report_hex";

    public void Dispose() => Stop();
}
