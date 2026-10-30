namespace Content.Server._Crescent.Territory;

[RegisterComponent, Access(typeof(PersistentPoiSaveSystem))]
public sealed partial class PersistentPoiSaveComponent : Component
{
    [DataField]
    public string Key = string.Empty;

    [DataField]
    public int MapVersion = 1;

    [DataField]
    public bool NativesStamped;
}

[RegisterComponent]
public sealed partial class PersistentPoiNativeComponent : Component;
