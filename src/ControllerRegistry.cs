using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Collections.Immutable;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Vector2 = Stride.Core.Mathematics.Vector2;
using Vector3 = Stride.Core.Mathematics.Vector3;

namespace VL.Devices.GearVR;

internal static class ControllerRegistry
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, GearVRBleDevice> Devices = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, byte>> ConnectionLeases = new(StringComparer.Ordinal);
    private static ControllerDescriptor[] _controllers = Array.Empty<ControllerDescriptor>();
    private static readonly ControllerListObservable ControllerListObservable = new();
    private static int _scanRequested;

    internal static void RequestScan()
    {
        if (Interlocked.Exchange(ref _scanRequested, 1) == 0)
            _ = Task.Run(ScanAsync);
    }

    internal static void EnsureControllerListForEnum()
    {
        // Package loading asks dynamic enums for their entries before a Process node is running.
        // Bluetooth enumeration must stay asynchronous here; otherwise Gamma can remain in
        // "adding dependency" while WinRT waits for the device-discovery apartment.
        RequestScan();
    }

    internal static GearVRControllerInfo GetSnapshot(string controllerName)
    {
        var descriptor = Volatile.Read(ref _controllers)
            .FirstOrDefault(item => string.Equals(item.ControllerName, controllerName, StringComparison.Ordinal));
        return descriptor is not null && Devices.TryGetValue(descriptor.DeviceId, out var device)
            ? device.Snapshot
            : GearVRControllerInfo.Searching(error: string.IsNullOrWhiteSpace(controllerName)
                ? "No paired Gear VR controller was found."
                : $"Bluetooth controller '{controllerName}' is not paired.");
    }

    internal static IReadOnlyDictionary<string, object> GetControllerEntries() =>
        Volatile.Read(ref _controllers).ToImmutableDictionary(item => item.ControllerName, item => (object)item.DeviceId, StringComparer.Ordinal);

    internal static IObservable<object> GetControllerEntriesChangedObservable() => ControllerListObservable;

    internal static string GetDefaultControllerName() => Volatile.Read(ref _controllers).FirstOrDefault()?.ControllerName ?? "";

    internal static void UpdateConnectionLease(long leaseId, string previousControllerName, string controllerName, bool allowSleep)
    {
        if (!string.Equals(previousControllerName, controllerName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(previousControllerName))
        {
            RemoveConnectionLease(previousControllerName, leaseId);
            ReconcileConnection(previousControllerName);
        }

        if (string.IsNullOrWhiteSpace(controllerName))
            return;

        ConnectionLeases.GetOrAdd(controllerName, _ => new ConcurrentDictionary<long, byte>())[leaseId] = allowSleep ? (byte)1 : (byte)0;

        ReconcileConnection(controllerName);
    }

    internal static void ReleaseConnectionLease(long leaseId, string controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName))
            return;

        RemoveConnectionLease(controllerName, leaseId);
        ReconcileConnection(controllerName);
    }

    private static void RemoveConnectionLease(string controllerName, long leaseId)
    {
        if (ConnectionLeases.TryGetValue(controllerName, out var leases))
            leases.TryRemove(leaseId, out _);
    }

    private static void ReconcileConnection(string controllerName, bool retryFailedConnection = false)
    {
        var descriptor = Volatile.Read(ref _controllers)
            .FirstOrDefault(item => string.Equals(item.ControllerName, controllerName, StringComparison.Ordinal));
        if (descriptor is null || !Devices.TryGetValue(descriptor.DeviceId, out var device))
            return;

        if (ConnectionLeases.TryGetValue(controllerName, out var leases) && !leases.IsEmpty)
        {
            device.EnsureConnected(retryFailedConnection);
            device.SetLowPowerMode(leases.Values.All(value => value != 0));
        }
        else
            device.ReleaseConnection();
    }

    private static async Task ScanAsync()
    {
        await ScanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelector();
            var found = await DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);
            var controllers = found
                .Where(IsGearVrController)
                .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            var descriptors = new List<ControllerDescriptor>(controllers.Length);
            foreach (var info in controllers)
            {
                var device = Devices.GetOrAdd(info.Id, _ => new GearVRBleDevice(info.Id, info.Name));
                descriptors.Add(new ControllerDescriptor(info.Name, info.Id));
            }
            Volatile.Write(ref _controllers, descriptors.ToArray());
            foreach (var descriptor in descriptors)
                ReconcileConnection(descriptor.ControllerName, retryFailedConnection: true);
            ControllerListObservable.NotifyChanged();
        }
        catch (Exception exception)
        {
            // The selected slot keeps its previous error/snapshot if scanning itself fails.
            _ = exception;
        }
        finally
        {
            Interlocked.Exchange(ref _scanRequested, 0);
            ScanGate.Release();
        }
    }

    private static bool IsGearVrController(DeviceInformation info) =>
        info.Name.Contains("Gear VR Controller", StringComparison.OrdinalIgnoreCase) ||
        info.Name.Contains("ET-YO", StringComparison.OrdinalIgnoreCase) ||
        info.Name.Contains("GearVR", StringComparison.OrdinalIgnoreCase);

    private sealed record ControllerDescriptor(string ControllerName, string DeviceId);
}

internal sealed class ControllerListObservable : IObservable<object>
{
    private readonly object _gate = new();
    private readonly List<IObserver<object>> _observers = new();

    public IDisposable Subscribe(IObserver<object> observer)
    {
        lock (_gate)
            _observers.Add(observer);
        return new Subscription(this, observer);
    }

    internal void NotifyChanged()
    {
        IObserver<object>[] observers;
        lock (_gate)
            observers = _observers.ToArray();
        foreach (var observer in observers)
            observer.OnNext(this);
    }

    private void Unsubscribe(IObserver<object> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private sealed class Subscription(ControllerListObservable owner, IObserver<object> observer) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(observer);
    }
}

internal sealed class GearVRBleDevice : IDisposable
{
    private static readonly Guid ControllerService = Guid.Parse("4f63756c-7573-2054-6872-65656d6f7465");
    private static readonly Guid CommandCharacteristic = Guid.Parse("c8c51726-81bc-483b-a052-f7a14ea3d282");
    private static readonly Guid DataCharacteristic = Guid.Parse("c8c51726-81bc-483b-a052-f7a14ea3d281");
    private static readonly Guid BatteryService = Guid.Parse("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryCharacteristic = Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb");
    private readonly string _id;
    private readonly string _name;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private int _connectionIntent;
    private int _intentRevision;
    private int _reconciling;
    private int _lowPowerModeRequested;
    private int _lowPowerModeApplied = -1;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _command;
    private GattCharacteristic? _data;
    private GattCharacteristic? _battery;
    private GearVRControllerInfo _snapshot;

    internal GearVRBleDevice(string id, string name)
    {
        _id = id;
        _name = name;
        _snapshot = GearVRControllerInfo.Searching(GearVRConnectionState.Disconnected);
    }

    internal GearVRControllerInfo Snapshot => Volatile.Read(ref _snapshot) with
    {
        AgeSeconds = Math.Max(0, (DateTime.UtcNow - _lastReceivedUtc).TotalSeconds)
    };

    private DateTime _lastReceivedUtc = DateTime.UtcNow;

    internal void EnsureConnected(bool retryFailedConnection = false)
    {
        if (Interlocked.Exchange(ref _connectionIntent, 1) != 1)
            Interlocked.Increment(ref _intentRevision);
        if (!IsReady && (retryFailedConnection || Volatile.Read(ref _snapshot).ConnectionState != GearVRConnectionState.Error))
            QueueReconcile();
    }

    internal void SetLowPowerMode(bool allowSleep)
    {
        var requested = allowSleep ? 1 : 0;
        if (Interlocked.Exchange(ref _lowPowerModeRequested, requested) != requested)
        {
            Interlocked.Increment(ref _intentRevision);
            QueueReconcile();
        }
    }

    internal void ReleaseConnection()
    {
        if (Interlocked.Exchange(ref _connectionIntent, 0) != 0)
            Interlocked.Increment(ref _intentRevision);
        if (HasResources)
            QueueReconcile();
    }

    private bool IsReady => _device is not null && _data is not null && Volatile.Read(ref _snapshot).ConnectionState == GearVRConnectionState.Connected;
    private bool HasResources => _device is not null || _command is not null || _data is not null || _battery is not null;

    private void QueueReconcile()
    {
        if (Interlocked.Exchange(ref _reconciling, 1) == 0)
            _ = Task.Run(ReconcileConnectionAsync);
    }

    private async Task ReconcileConnectionAsync()
    {
        var processedRevision = 0;
        try
        {
            await _connectionGate.WaitAsync().ConfigureAwait(false);
            processedRevision = Volatile.Read(ref _intentRevision);
            if (Volatile.Read(ref _connectionIntent) == 0)
            {
                await DisconnectAsync().ConfigureAwait(false);
                return;
            }

            if (!IsReady)
            {
                if (HasResources)
                    await ReleaseResourcesAsync().ConfigureAwait(false);
                await ConnectAsync().ConfigureAwait(false);
            }

            if (IsReady)
                await ApplyLowPowerModeAsync().ConfigureAwait(false);

            if (Volatile.Read(ref _connectionIntent) == 0)
                await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetState(GearVRConnectionState.Error, exception.Message);
        }
        finally
        {
            if (_connectionGate.CurrentCount == 0)
                _connectionGate.Release();
            Interlocked.Exchange(ref _reconciling, 0);
            if (Volatile.Read(ref _intentRevision) != processedRevision)
                QueueReconcile();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            SetState(GearVRConnectionState.Connecting, "");
            _device = await BluetoothLEDevice.FromIdAsync(_id).AsTask().ConfigureAwait(false);
            if (_device is null)
                throw new InvalidOperationException("Windows could not open the paired Bluetooth device.");
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            var services = await GetServicesWithRetryAsync(_device).ConfigureAwait(false);

            var controllerService = services.Services.FirstOrDefault(service => service.Uuid == ControllerService)
                ?? throw new InvalidOperationException("Gear VR controller GATT service was not found.");
            var access = await controllerService.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
                throw new InvalidOperationException($"Access to the Gear VR controller service was denied: {access}.");
            var characteristics = await controllerService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            _command = characteristics.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == CommandCharacteristic)
                ?? throw new InvalidOperationException("Gear VR command characteristic was not found.");
            _data = characteristics.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == DataCharacteristic)
                ?? throw new InvalidOperationException("Gear VR data characteristic was not found.");

            // The controller only starts its report stream after this exact setup handshake.
            // The 0x01 and 0x08 commands must be repeated three times; writing without a
            // response silently leaves many ET-YO324 units connected but idle.
            await SendCommandAsync(0x01, 3).ConfigureAwait(false);
            await SendCommandAsync(0x06, 1).ConfigureAwait(false);
            await SendCommandAsync(0x07, 1).ConfigureAwait(false);
            await SendCommandAsync(0x08, 3).ConfigureAwait(false);

            var notifyStatus = await _data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
            if (notifyStatus != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Unable to subscribe to controller data: {notifyStatus}.");
            _data.ValueChanged += OnData;
            await SubscribeBatteryAsync(services.Services).ConfigureAwait(false);
            SetState(GearVRConnectionState.Connected, "");
        }
        catch (Exception exception)
        {
            await ReleaseResourcesAsync().ConfigureAwait(false);
            SetState(GearVRConnectionState.Error, exception.Message);
        }
    }

    private async Task ApplyLowPowerModeAsync()
    {
        var requested = Volatile.Read(ref _lowPowerModeRequested);
        if (Volatile.Read(ref _lowPowerModeApplied) == requested)
            return;

        await SendCommandAsync(requested == 1 ? (byte)0x06 : (byte)0x07, 1).ConfigureAwait(false);
        Volatile.Write(ref _lowPowerModeApplied, requested);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            return;

        SetState(GearVRConnectionState.Disconnected, "");
        if (Volatile.Read(ref _connectionIntent) == 1 && Volatile.Read(ref _lowPowerModeRequested) == 0)
            QueueReconcile();
    }

    private async Task DisconnectAsync()
    {
        await ReleaseResourcesAsync().ConfigureAwait(false);
        SetState(GearVRConnectionState.Disconnected, "");
    }

    private async Task ReleaseResourcesAsync()
    {
        Volatile.Write(ref _lowPowerModeApplied, -1);
        var data = _data;
        _data = null;
        if (data is not null)
        {
            data.ValueChanged -= OnData;
            try
            {
                await data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().ConfigureAwait(false);
            }
            catch
            {
                // The device may already be asleep; disposing the Windows handle is still enough to release it.
            }
        }

        var battery = _battery;
        _battery = null;
        if (battery is not null)
        {
            battery.ValueChanged -= OnBattery;
            try
            {
                await battery.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().ConfigureAwait(false);
            }
            catch
            {
                // See the data-characteristic cleanup above.
            }
        }

        _command = null;
        var device = _device;
        _device = null;
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
        }
    }

    private async Task SubscribeBatteryAsync(IReadOnlyList<GattDeviceService> services)
    {
        var service = services.FirstOrDefault(item => item.Uuid == BatteryService);
        if (service is null)
            return;
        var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        _battery = characteristics.Characteristics.FirstOrDefault(item => item.Uuid == BatteryCharacteristic);
        if (_battery is null)
            return;
        _battery.ValueChanged += OnBattery;
        await _battery.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
        var current = await _battery.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (current.Status == GattCommunicationStatus.Success)
            UpdateBattery(ReadBuffer(current.Value));
    }

    private static async Task<GattDeviceServicesResult> GetServicesWithRetryAsync(BluetoothLEDevice device)
    {
        GattDeviceServicesResult? lastResult = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            lastResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            if (lastResult.Status == GattCommunicationStatus.Success && lastResult.Services.Count >= 3)
                return lastResult;

            // ET-YO324 can report Unreachable while waking from sleep. Reusing the opened
            // BluetoothLEDevice mirrors the retry strategy of the working Windows reference.
            await Task.Delay(350).ConfigureAwait(false);
        }

        var status = lastResult?.Status.ToString() ?? "Unknown";
        throw new InvalidOperationException($"Unable to read GATT services after six attempts: {status}. Wake the controller and rescan.");
    }

    private async Task SendCommandAsync(byte command, int repeat)
    {
        var bytes = CryptographicBuffer.CreateFromByteArray([command, 0]);
        while (repeat-- > 0)
        {
            var status = await _command!.WriteValueAsync(bytes, GattWriteOption.WriteWithResponse).AsTask().ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"Gear VR setup command 0x{command:X2} failed: {status}.");
        }
    }

    private void OnBattery(GattCharacteristic sender, GattValueChangedEventArgs args) => UpdateBattery(ReadBuffer(args.CharacteristicValue));

    private void UpdateBattery(byte[] bytes)
    {
        if (bytes.Length == 0)
            return;
        var previous = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, previous with { BatteryPercent = bytes[0] });
    }

    private void OnData(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var report = ReadBuffer(args.CharacteristicValue);
        if (report.Length < 60)
            return;

        var previous = Volatile.Read(ref _snapshot);
        var touchX = (((report[54] & 0x0f) << 6) | ((report[55] & 0xfc) >> 2)) & 0x3ff;
        var touchY = (((report[55] & 0x03) << 8) | report[56]) & 0x3ff;
        var touched = touchX != 0 || touchY != 0;
        var flags = report[58];
        // The Samsung input service decodes all IMU shorts as little-endian.
        // Reading them as big-endian produces discontinuous, unusable orientation data.
        var rawAcceleration = new Vector3(ReadInt16LittleEndian(report, 4), ReadInt16LittleEndian(report, 6), ReadInt16LittleEndian(report, 8));
        var rawGyroscope = new Vector3(ReadInt16LittleEndian(report, 10), ReadInt16LittleEndian(report, 12), ReadInt16LittleEndian(report, 14));
        // Bytes 32..47 are the third accel/gyro sample in this 60-byte notification.
        // The three magnetometer words are the stable tail at 48..53.
        var rawMagnetometer = new Vector3(ReadInt16LittleEndian(report, 48), ReadInt16LittleEndian(report, 50), ReadInt16LittleEndian(report, 52));
        _lastReceivedUtc = DateTime.UtcNow;
        Volatile.Write(ref _snapshot, new GearVRControllerInfo(
            GearVRConnectionState.Connected, _name, _id, "", previous.Sequence + 1,
            BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(0, 4)), 0, previous.BatteryPercent, report[57],
            rawAcceleration, Scale(rawAcceleration, 0.00478840332f),
            rawGyroscope, Scale(rawGyroscope, 0.001221791529f),
            rawMagnetometer, Scale(rawMagnetometer, 0.06f),
            touchX, touchY, new Vector2(touchX / 315f, touchY / 315f), touched,
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
            (flags & 16) != 0, (flags & 32) != 0, (flags & 64) != 0, report));
    }

    private void SetState(GearVRConnectionState state, string error)
    {
        var previous = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, previous with { ConnectionState = state, Name = _name, DeviceId = _id, Error = error });
    }

    private static short ReadInt16LittleEndian(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
    private static Vector3 Scale(Vector3 value, float factor) => new(value.X * factor, value.Y * factor, value.Z * factor);
    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        ReleaseConnection();
    }
}
