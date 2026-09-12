using Vector2 = Stride.Core.Mathematics.Vector2;
using Vector3 = Stride.Core.Mathematics.Vector3;

namespace VL.Devices.GearVR;

/// <summary>One of the eight independently addressable controller slots.</summary>
public enum GearVRControllerSlot
{
    Slot0,
    Slot1,
    Slot2,
    Slot3,
    Slot4,
    Slot5,
    Slot6,
    Slot7
}

/// <summary>Connection state of the selected controller.</summary>
public enum GearVRConnectionState
{
    Searching,
    Connecting,
    Connected,
    Disconnected,
    Error
}

/// <summary>Eight-way touchpad direction in screen coordinates.</summary>
public enum GearVRSwipeDirection
{
    None,
    Up,
    UpRight,
    Right,
    DownRight,
    Down,
    DownLeft,
    Left,
    UpLeft
}

/// <summary>Recognised touchpad event. Gesture outputs are one-frame pulses.</summary>
public enum GearVRTouchGesture
{
    None,
    Tap,
    DoubleTap,
    LongPress,
    SwipeUp,
    SwipeUpRight,
    SwipeRight,
    SwipeDownRight,
    SwipeDown,
    SwipeDownLeft,
    SwipeLeft,
    SwipeUpLeft,
    CircleClockwise,
    CircleCounterClockwise
}

/// <summary>
/// Complete, immutable reading from a Gear VR controller. Raw vectors are sensor counts;
/// scaled vectors use m/s², rad/s and µT respectively.
/// </summary>
public sealed record GearVRControllerInfo(
    GearVRConnectionState ConnectionState,
    string Name,
    string DeviceId,
    string Error,
    long Sequence,
    long DeviceTimestampMicroseconds,
    double AgeSeconds,
    int BatteryPercent,
    int TemperatureCelsius,
    Vector3 RawAcceleration,
    Vector3 Acceleration,
    Vector3 RawGyroscope,
    Vector3 AngularVelocity,
    Vector3 RawMagnetometer,
    Vector3 MagneticField,
    int TouchX,
    int TouchY,
    Vector2 TouchPosition,
    bool IsTouched,
    bool Trigger,
    bool Home,
    bool Back,
    bool TouchpadClick,
    bool VolumeUp,
    bool VolumeDown,
    bool NoButton,
    byte[] RawReport)
{
    internal static GearVRControllerInfo Searching(GearVRConnectionState state = GearVRConnectionState.Searching, string error = "") =>
        new(state, "", "", error, 0, 0, double.PositiveInfinity, -1, -1,
            default, default, default, default, default, default, 0, 0, default, false,
            false, false, false, false, false, false, false, Array.Empty<byte>());
}
