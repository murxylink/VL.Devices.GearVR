using VL.Core.Import;
using Vector2 = Stride.Core.Mathematics.Vector2;

namespace VL.Devices.GearVR;

/// <summary>Exposes touchpad coordinates, deltas, eight-way swipes, taps, long presses and circular motions.</summary>
[ProcessNode(Name = "Touchpad", Category = "Devices.GearVR")]
public sealed class Touchpad
{
    private const float PadSize = 315f;
    private const float SwipeDistance = 0.18f;
    private const float TapDistance = 0.055f;
    private const double TapSeconds = 0.25;
    private const double DoubleTapSeconds = 0.35;
    private const double LongPressSeconds = 0.65;

    private long _sequence = -1;
    private bool _wasTouched;
    private Vector2 _start;
    private Vector2 _previous;
    private long _startTime;
    private long _lastTapTime;
    private float _circleAngle;
    private float _circleTravel;
    private bool _longPressSent;

    /// <summary>Outputs coordinates and the latest recognised gesture. Touch position is normalised to 0–1.</summary>
    public void Update(
        out Vector2 position,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out int rawX,
        [Pin(Visibility = VL.Model.PinVisibility.Optional)] out int rawY,
        out Vector2 delta,
        out bool touched,
        out bool pressed,
        out GearVRTouchGesture gesture,
        out GearVRSwipeDirection swipeDirection,
        GearVRController? controller = null)
    {
        var info = controller?.GetSnapshot() ?? GearVRControllerInfo.Searching();
        position = info.TouchPosition;
        rawX = info.TouchX;
        rawY = info.TouchY;
        touched = info.IsTouched;
        pressed = info.TouchpadClick;
        delta = default;
        gesture = GearVRTouchGesture.None;
        swipeDirection = GearVRSwipeDirection.None;

        if (info.Sequence == _sequence || info.Sequence == 0)
            return;

        _sequence = info.Sequence;
        var now = info.DeviceTimestampMicroseconds;
        if (touched && !_wasTouched)
        {
            _start = _previous = position;
            _startTime = now;
            _circleAngle = Angle(position);
            _circleTravel = 0;
            _longPressSent = false;
        }
        else if (touched && _wasTouched)
        {
            delta = new Vector2(position.X - _previous.X, position.Y - _previous.Y);
            _previous = position;
            var radius = Distance(position, new Vector2(0.5f, 0.5f));
            if (radius > 0.24f)
            {
                var angle = Angle(position);
                var change = WrapAngle(angle - _circleAngle);
                _circleTravel += change;
                _circleAngle = angle;
            }

            if (!_longPressSent && Elapsed(now, _startTime) >= LongPressSeconds && Distance(position, _start) < TapDistance)
            {
                gesture = GearVRTouchGesture.LongPress;
                _longPressSent = true;
            }
        }
        else if (!touched && _wasTouched)
        {
            var duration = Elapsed(now, _startTime);
            // The controller reports 0/0 after finger lift, so evaluate the gesture at
            // the final valid contact coordinate retained in _previous.
            var end = _previous;
            var travel = Distance(end, _start);
            if (Math.Abs(_circleTravel) >= MathF.PI * 1.65f)
            {
                gesture = _circleTravel > 0 ? GearVRTouchGesture.CircleClockwise : GearVRTouchGesture.CircleCounterClockwise;
            }
            else if (travel >= SwipeDistance)
            {
                swipeDirection = Direction(end.X - _start.X, end.Y - _start.Y);
                gesture = ToSwipeGesture(swipeDirection);
            }
            else if (!_longPressSent && duration <= TapSeconds && travel <= TapDistance)
            {
                if (_lastTapTime != 0 && Elapsed(now, _lastTapTime) <= DoubleTapSeconds)
                {
                    gesture = GearVRTouchGesture.DoubleTap;
                    _lastTapTime = 0;
                }
                else
                {
                    gesture = GearVRTouchGesture.Tap;
                    _lastTapTime = now;
                }
            }
        }
        _wasTouched = touched;
    }

    private static float Distance(Vector2 a, Vector2 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Elapsed(long later, long earlier) => later > earlier ? (later - earlier) / 1_000_000.0 : 0;
    private static float Angle(Vector2 position) => MathF.Atan2(position.Y - 0.5f, position.X - 0.5f);
    private static float WrapAngle(float value) => value > MathF.PI ? value - MathF.Tau : value < -MathF.PI ? value + MathF.Tau : value;

    private static GearVRSwipeDirection Direction(float x, float y)
    {
        var octant = (int)MathF.Round(MathF.Atan2(y, x) / (MathF.PI / 4));
        return (((octant % 8) + 8) % 8) switch
        {
            0 => GearVRSwipeDirection.Right,
            1 => GearVRSwipeDirection.DownRight,
            2 => GearVRSwipeDirection.Down,
            3 => GearVRSwipeDirection.DownLeft,
            4 => GearVRSwipeDirection.Left,
            5 => GearVRSwipeDirection.UpLeft,
            6 => GearVRSwipeDirection.Up,
            _ => GearVRSwipeDirection.UpRight
        };
    }

    private static GearVRTouchGesture ToSwipeGesture(GearVRSwipeDirection direction) => direction switch
    {
        GearVRSwipeDirection.Up => GearVRTouchGesture.SwipeUp,
        GearVRSwipeDirection.UpRight => GearVRTouchGesture.SwipeUpRight,
        GearVRSwipeDirection.Right => GearVRTouchGesture.SwipeRight,
        GearVRSwipeDirection.DownRight => GearVRTouchGesture.SwipeDownRight,
        GearVRSwipeDirection.Down => GearVRTouchGesture.SwipeDown,
        GearVRSwipeDirection.DownLeft => GearVRTouchGesture.SwipeDownLeft,
        GearVRSwipeDirection.Left => GearVRTouchGesture.SwipeLeft,
        GearVRSwipeDirection.UpLeft => GearVRTouchGesture.SwipeUpLeft,
        _ => GearVRTouchGesture.None
    };
}
