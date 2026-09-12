using System.Numerics;
using System.Diagnostics;
using VL.Core.Import;
using StrideQuaternion = Stride.Core.Mathematics.Quaternion;
using StrideVector3 = Stride.Core.Mathematics.Vector3;

namespace VL.Devices.GearVR;

/// <summary>
/// Provides raw IMU vectors, useful processed acceleration and gravity-corrected orientation values.
/// Heading remains relative until a separately calibrated magnetometer is used.
/// </summary>
[ProcessNode(Name = "Movement", Category = "Devices.GearVR")]
public sealed class Movement
{
    private long _sequence = -1;
    private long _lastHostTicks;
    private uint _lastDeviceTimestamp;
    private bool _hasDeviceTimestamp;
    private Vector3 _gravity;
    private Vector3 _linearAcceleration;
    private Vector3 _gyroscopeBias;
    private Quaternion _orientation = Quaternion.Identity;
    private bool _hasGravity;
    private bool _needsOrientationInitialization = true;
    private bool _isCalibrated;
    private bool _previousCalibrate;
    private bool _isCalibrating;
    private int _calibrationSamples;
    private Vector3 _calibrationGyroscopeSum;
    private float _gravityMagnitude = 9.80665f;

    /// <summary>Outputs raw data, scaled IMU values, gravity-removed acceleration and a relative orientation.</summary>
    public void Update(
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 rawAcceleration,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 acceleration,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 rawGyroscope,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 angularVelocity,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 rawMagnetometer,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 magneticField,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 gravity,
        out StrideVector3 linearAcceleration,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out StrideVector3 calibratedAngularVelocity,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out float accelerationMagnitude,
        out StrideQuaternion orientation,
        out StrideVector3 rotationDegrees,
        out bool isCalibrated,
        out bool isCalibrating,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out float calibrationProgress,
        GearVRController? controller = null,
        bool resetOrientation = false,
        bool calibrate = false,
        float gravityResponseSeconds = 0.65f,
        float orientationCorrection = 2.0f)
    {
        var info = controller?.GetSnapshot() ?? GearVRControllerInfo.Searching();
        rawAcceleration = info.RawAcceleration;
        acceleration = info.Acceleration;
        rawGyroscope = info.RawGyroscope;
        rawMagnetometer = info.RawMagnetometer;
        magneticField = info.MagneticField;

        if (resetOrientation)
        {
            _orientation = Quaternion.Identity;
            _lastHostTicks = 0;
            _hasDeviceTimestamp = false;
            _needsOrientationInitialization = true;
        }
        if (calibrate && !_previousCalibrate)
        {
            StartCalibration();
        }
        _previousCalibrate = calibrate;

        if (info.Sequence != _sequence && info.Sequence != 0)
        {
            var hostTicks = Stopwatch.GetTimestamp();
            var hostDeltaSeconds = _lastHostTicks == 0 ? 0.0 : (hostTicks - _lastHostTicks) / (double)Stopwatch.Frequency;
            _lastHostTicks = hostTicks;
            _sequence = info.Sequence;
            var deltaSeconds = GetDeltaSeconds(info.DeviceTimestampMicroseconds, hostDeltaSeconds);
            var dt = (float)Math.Clamp(deltaSeconds, 0.0, 0.1);
            var sourceAcceleration = ToNumerics(info.Acceleration);
            var rawOmega = ToNumerics(info.AngularVelocity);
            var accelerationLength = sourceAcceleration.Length();

            if (!_hasGravity)
            {
                _gravity = sourceAcceleration;
                _hasGravity = true;
            }
            if (!_isCalibrated && !_isCalibrating)
                StartCalibration();

            var accelerationUnit = NormalizeOrZero(sourceAcceleration);
            var accelerationLooksLikeGravity = accelerationLength is > 7.0f and < 13.0f;
            if (_needsOrientationInitialization && accelerationUnit != Vector3.Zero)
            {
                // Establish a level, repeatable origin from gravity. Yaw intentionally stays zero.
                _orientation = RotationFromTo(accelerationUnit, Vector3.UnitZ);
                _needsOrientationInitialization = false;
            }

            var response = Math.Max(gravityResponseSeconds, 0.001f);
            var alpha = dt <= 0 ? 0.02f : dt / (response + dt);

            // A calibration run gathers one second of still samples. It starts automatically
            // on the first still controller report, or manually on Calibrate's rising edge.
            if (_isCalibrating)
            {
                if (accelerationLooksLikeGravity && rawOmega.Length() < 0.20f)
                {
                    _calibrationGyroscopeSum += rawOmega;
                    _calibrationSamples++;
                    _gravityMagnitude = Lerp(_gravityMagnitude, accelerationLength, 0.04f);
                    if (_calibrationSamples >= 55)
                    {
                        _gyroscopeBias = _calibrationGyroscopeSum / _calibrationSamples;
                        _isCalibrated = true;
                        _isCalibrating = false;
                    }
                }
            }
            else if (_isCalibrated && accelerationLooksLikeGravity && rawOmega.Length() < 0.10f)
            {
                // Very slow rest tracking compensates temperature drift without cancelling
                // intentional controller movement.
                _gyroscopeBias = Vector3.Lerp(_gyroscopeBias, rawOmega, 0.001f);
                _gravityMagnitude = Lerp(_gravityMagnitude, accelerationLength, alpha * 0.1f);
            }

            var omega = rawOmega - _gyroscopeBias;
            if (accelerationLooksLikeGravity && accelerationUnit != Vector3.Zero)
            {
                // Mahony-style 6-axis correction: accelerometer locks pitch/roll while the
                // gyro supplies smooth motion. The magnetometer is deliberately not used here
                // until it has a proper hard/soft-iron calibration.
                var expectedUpInController = NormalizeOrZero(Vector3.Transform(Vector3.UnitZ, Quaternion.Inverse(_orientation)));
                var error = Vector3.Cross(accelerationUnit, expectedUpInController);
                var correction = Math.Clamp(orientationCorrection, 0.0f, 12.0f);
                omega += error * correction;
                if (dt > 0)
                    _gyroscopeBias -= error * (0.04f * dt);
            }

            var speed = omega.Length();
            if (speed is > 0.0001f and < 20f && dt > 0)
            {
                var step = Quaternion.CreateFromAxisAngle(Vector3.Normalize(omega), speed * dt);
                // Gyro rates are expressed in the controller's local axes. Append the small
                // local rotation on the right; Concatenate applies the second rotation in the
                // world frame and makes a sideways controller appear to rotate around mixed axes.
                _orientation = Quaternion.Normalize(Quaternion.Multiply(_orientation, step));
            }

            // Keep the simple linear-acceleration output responsive, but use the corrected
            // attitude as its long-term gravity reference rather than following hand motion.
            var expectedGravity = NormalizeOrZero(Vector3.Transform(Vector3.UnitZ, Quaternion.Inverse(_orientation))) * _gravityMagnitude;
            _gravity = Vector3.Lerp(_gravity, expectedGravity, alpha);
            _linearAcceleration = sourceAcceleration - _gravity;
        }

        angularVelocity = info.AngularVelocity;
        gravity = ToStride(_gravity);
        linearAcceleration = ToStride(_linearAcceleration);
        calibratedAngularVelocity = ToStride(ToNumerics(info.AngularVelocity) - _gyroscopeBias);
        accelerationMagnitude = _linearAcceleration.Length();
        var correctedOrientation = CorrectOrientation(_orientation);
        orientation = new StrideQuaternion(correctedOrientation.X, correctedOrientation.Y, correctedOrientation.Z, correctedOrientation.W);
        rotationDegrees = ToStride(ToEulerDegrees(correctedOrientation));
        isCalibrated = _isCalibrated;
        isCalibrating = _isCalibrating;
        calibrationProgress = Math.Clamp(_calibrationSamples / 55f, 0f, 1f);
    }

    private void StartCalibration()
    {
        _orientation = Quaternion.Identity;
        _lastHostTicks = 0;
        _hasDeviceTimestamp = false;
        _hasGravity = false;
        _needsOrientationInitialization = true;
        _gyroscopeBias = Vector3.Zero;
        _isCalibrated = false;
        _isCalibrating = true;
        _calibrationSamples = 0;
        _calibrationGyroscopeSum = Vector3.Zero;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f ? value / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }

    private static float Lerp(float left, float right, float amount) => left + (right - left) * amount;

    private double GetDeltaSeconds(long timestampMicroseconds, double hostDeltaSeconds)
    {
        if (timestampMicroseconds is <= 0 or > uint.MaxValue)
            return hostDeltaSeconds;

        var timestamp = (uint)timestampMicroseconds;
        if (!_hasDeviceTimestamp)
        {
            _lastDeviceTimestamp = timestamp;
            _hasDeviceTimestamp = true;
            return 0;
        }

        var deltaMicroseconds = unchecked(timestamp - _lastDeviceTimestamp);
        _lastDeviceTimestamp = timestamp;
        // Sensor timestamp wraps at UInt32.MaxValue. Implausible jumps occur when a player loop
        // restarts and fall back to host timing for that one update.
        var sensorDeltaSeconds = deltaMicroseconds / 1_000_000.0;
        return sensorDeltaSeconds is > 0 and <= 0.1 ? sensorDeltaSeconds : hostDeltaSeconds;
    }

    private static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.999999f)
            return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            var axis = NormalizeOrZero(Vector3.Cross(from, Vector3.UnitX));
            if (axis == Vector3.Zero)
                axis = Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot));
    }

    // Gamma's standard coordinate convention requires the controller's Y axis to become
    // negative Z, while its Z axis becomes Y. Keep fusion in sensor coordinates and adapt
    // only the publicly exposed orientation.
    private static Quaternion CorrectOrientation(Quaternion value) =>
        Quaternion.Normalize(new Quaternion(value.X, value.Z, -value.Y, value.W));

    private static Vector3 ToNumerics(StrideVector3 value) => new(value.X, value.Y, value.Z);
    private static StrideVector3 ToStride(Vector3 value) => new(value.X, value.Y, value.Z);

    private static Vector3 ToEulerDegrees(Quaternion q)
    {
        var sinrCosp = 2 * (q.W * q.X + q.Y * q.Z);
        var cosrCosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        var roll = Math.Atan2(sinrCosp, cosrCosp);
        var sinp = 2 * (q.W * q.Y - q.Z * q.X);
        var pitch = Math.Abs(sinp) >= 1 ? Math.CopySign(Math.PI / 2, sinp) : Math.Asin(sinp);
        var sinyCosp = 2 * (q.W * q.Z + q.X * q.Y);
        var cosyCosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var yaw = Math.Atan2(sinyCosp, cosyCosp);
        const float radiansToDegrees = 180f / MathF.PI;
        return new Vector3((float)roll * radiansToDegrees, (float)pitch * radiansToDegrees, (float)yaw * radiansToDegrees);
    }
}
