using VL.Core.Import;

namespace VL.Devices.GearVR;

/// <summary>Exposes held button states for a Gear VR controller.</summary>
[ProcessNode(Name = "Clicks", Category = "Devices.GearVR")]
public sealed class Clicks
{
    /// <summary>Outputs current button states.</summary>
    public void Update(
        out bool trigger,
        out bool home,
        out bool back,
        out bool touchpad,
        out bool volumeUp,
        out bool volumeDown,
        GearVRController? controller = null)
    {
        var info = controller?.GetSnapshot() ?? GearVRControllerInfo.Searching();
        trigger = info.Trigger;
        home = info.Home;
        back = info.Back;
        touchpad = info.TouchpadClick;
        volumeUp = info.VolumeUp;
        volumeDown = info.VolumeDown;
    }
}
