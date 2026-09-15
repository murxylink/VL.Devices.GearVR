# VL.Devices.GearVR

Windows Bluetooth LE nodes for Samsung Gear VR / ET-YO324 controllers in vvvv gamma 7.4 or later. One package manages up to eight paired controllers.

## Install

1. Pair every controller in **Windows Settings > Bluetooth & devices** first. Do not connect it as a mouse or gamepad application.
2. In a command prompt opened in Gamma's package folder (for example, `C:\NugetOverride\7.4`), install the public package from NuGet.org:

   ```powershell
   nuget install VL.Devices.GearVR -Version 1.2.3 -Source https://api.nuget.org/v3/index.json
   ```

   Restart Gamma after the installation.
3. Add **GearVR Controller**. Its initial search completes before the **Bluetooth Controller** input is created, so the first paired controller is selected automatically rather than `NULL`. The dropdown displays full Windows Bluetooth names, such as `Gear VR Controller(5951)`. **Active Controller** shows the actual choice. Set **Rescan** to `true` for one frame to refresh the dropdown, or select another controller manually.
4. Connect its **Controller** output to **Clicks**, **Movement**, and/or **Touchpad**.

## Nodes

| Node | What it provides |
| --- | --- |
| `GearVR Controller` | Slot selection, rescan, controller handle, full immutable `Info`, and battery percentage. Set `Allow Sleep` to enable the controller's low-power mode: it continues working until its firmware chooses to sleep after inactivity. Set it back to `false` to keep the controller awake. `Info.Raw Report` is the original 60-byte BLE frame. |
| `Clicks` | Current held state for Trigger, Home, Back, Touchpad click, Volume Up and Volume Down. |
| `Movement` | Raw sensor counts; scaled acceleration (m/s²), angular velocity (rad/s), magnetic field (µT); auto-calibrated gyro velocity, gravity-removed acceleration, and a 6-axis (gyro + gravity) orientation. |
| `Touchpad` | Raw and normalised coordinates, delta, touch/click state, and the latest recognised Tap, Double Tap, Long Press, eight-way swipe or circle gesture. |
| `Sensor Recorder` | Writes every incoming BLE report and all decoded sensor/control fields to CSV for diagnostics. |
| `Sensor Player` | Replays a Sensor Recorder CSV without Bluetooth hardware. Its `Controller` output connects to `Clicks`, `Movement`, `Touchpad`, and `Sensor Recorder` exactly like a live controller. |

`Movement` calibrates automatically after one second of stillness; `Calibrate` restarts that process. `Orientation` uses gravity to keep pitch and roll stable and has relative yaw. Use **Reset Orientation** for a new yaw zero. The magnetometer is exposed but not used for heading correction until it has location-specific hard/soft-iron calibration.

## Offline playback

Add **Sensor Player**, set **File Path** to a CSV written by **Sensor Recorder**, and connect its **Controller** output where a live controller would be used. Set **Play** to start, use **Speed** for 0–16× realtime, **Loop** to repeat, or pulse **Restart** to return to the first sample. Recorded device timestamps preserve IMU integration timing in `Movement` even when playback speed differs from realtime.

## Help patches

Installed help patches are available in the package's `help` folder: `Overview`, `Preview in Stride`, and `Record and Playback`. The folder also contains their model and CSV playback assets.

## Controller selection

The **Bluetooth Controller** enum is dynamic: it has one entry per discovered, paired controller and displays its full Windows Bluetooth name, for example `Gear VR Controller(5951)`. Rescan after adding, removing, or waking a controller. The selection is bound to the device's Bluetooth ID, not its list position. `Info.Connection State`, `Info.Name`, and `Info.Error` make connection failures visible in the patch.

## Protocol basis

The BLE service UUID and report layout follow independent open-source reverse engineering by [rdady](https://github.com/rdady/gear-vr-controller-windows) and [uutzinger](https://github.com/uutzinger/gearVRC). This package is an original C# Gamma implementation.
