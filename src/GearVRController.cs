using System.Threading;
using VL.Core.Import;

namespace VL.Devices.GearVR;

/// <summary>
/// Selects a paired Gear VR controller, connects it over Bluetooth LE and exposes its latest full report.
/// Set Rescan to true once after pairing or when controllers are added or removed.
/// </summary>
[ProcessNode(Name = "GearVR Controller", Category = "Devices.GearVR")]
public sealed class GearVRController : IDisposable
{
    private bool _previousRescan;
    private bool _started;
    private readonly bool _isPlaybackController;
    private string _controllerName = "";
    private GearVRControllerInfo? _playbackSnapshot;

    /// <summary>Creates a Bluetooth controller handle for the GearVR Controller node.</summary>
    public GearVRController()
    {
    }

    internal GearVRController(bool isPlaybackController)
    {
        _isPlaybackController = isPlaybackController;
        _playbackSnapshot = GearVRControllerInfo.Searching();
    }

    /// <summary>Gets the selected controller handle and its complete most-recent reading.</summary>
    public void Update(
        out GearVRController controller,
        out GearVRControllerInfo info,
        out int battery,
        out string activeController,
        [Pin(Name = "Bluetooth Controller")] GearVRControllerSerialNumber controllerSelection,
        bool rescan = false)
    {
        if (_isPlaybackController)
        {
            info = GetSnapshot();
            battery = info.BatteryPercent;
            activeController = "";
            controller = this;
            return;
        }

        _controllerName = string.IsNullOrWhiteSpace(controllerSelection?.Value)
            ? ControllerRegistry.GetDefaultControllerName()
            : controllerSelection.Value;
        if (!_started || (rescan && !_previousRescan))
        {
            ControllerRegistry.RequestScan();
            _started = true;
        }

        _previousRescan = rescan;
        info = ControllerRegistry.GetSnapshot(_controllerName);
        battery = info.BatteryPercent;
        activeController = _controllerName;
        controller = this;
    }

    internal GearVRControllerInfo GetSnapshot() => _isPlaybackController
        ? _playbackSnapshot ?? GearVRControllerInfo.Searching()
        : ControllerRegistry.GetSnapshot(_controllerName);

    internal void SetPlaybackSnapshot(GearVRControllerInfo snapshot)
    {
        if (_isPlaybackController)
            _playbackSnapshot = snapshot;
    }

    public void Dispose()
    {
        // Connections are shared by the eight-slot registry and intentionally live while Gamma runs.
    }
}
