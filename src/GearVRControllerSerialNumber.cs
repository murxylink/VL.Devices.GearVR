using VL.Core.CompilerServices;
using VL.Lib.Collections;

namespace VL.Devices.GearVR;

/// <summary>Runtime list of Bluetooth names for paired Gear VR controllers found by Rescan.</summary>
[Serializable]
public sealed class GearVRControllerSerialNumber : DynamicEnumBase<GearVRControllerSerialNumber, GearVRControllerSerialNumberDefinition>
{
    public GearVRControllerSerialNumber(string value) : base(value)
    {
    }

    [CreateDefault]
    public static GearVRControllerSerialNumber CreateDefault()
    {
        ControllerRegistry.EnsureControllerListForEnum();
        return CreateDefaultBase();
    }
}

/// <summary>Supplies the Bluetooth-name entries used by GearVR Controller.</summary>
public sealed class GearVRControllerSerialNumberDefinition : DynamicEnumDefinitionBase<GearVRControllerSerialNumberDefinition>
{
    protected override IReadOnlyDictionary<string, object> GetEntries()
    {
        // Populate the enum before the editor asks DynamicEnumBase for its default value.
        ControllerRegistry.EnsureControllerListForEnum();
        return ControllerRegistry.GetControllerEntries();
    }

    protected override IObservable<object> GetEntriesChangedObservable() => ControllerRegistry.GetControllerEntriesChangedObservable();

    protected override bool AutoSortAlphabetically => true;
}
