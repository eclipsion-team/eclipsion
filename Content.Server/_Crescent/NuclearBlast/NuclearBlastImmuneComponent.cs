namespace Content.Server._Crescent.NuclearBlast;

/// <summary>
/// Survives a <see cref="NuclearBlastComponent"/> blast. For round-critical objectives that must not silently vanish.
/// </summary>
[RegisterComponent]
public sealed partial class NuclearBlastImmuneComponent : Component;
