using Robust.Shared.GameStates;

namespace Content.Shared.Crescent.Psionics;

/// <summary>
/// χ Waveform Misalignment turned outwards. Every psion standing within <see cref="Range"/> of this entity loses
/// the noosphere - no casting, and whatever they had running collapses - and anything psionic sent at this entity
/// comes apart before it lands.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PsionicNullifierComponent : Component
{
    [DataField]
    public float Range = 5f;

    /// <summary>
    /// Lent by worn gear rather than carried from birth, so taking that gear off takes the field away again.
    /// </summary>
    [DataField]
    public bool FromClothing;

    /// <summary>
    /// The item projecting the field when <see cref="FromClothing"/> is set.
    /// </summary>
    [ViewVariables]
    public EntityUid? Source;
}

/// <summary>
/// Headgear that hands its wearer a <see cref="PsionicNullifierComponent"/> and the insulation that goes with it.
/// </summary>
[RegisterComponent]
public sealed partial class PsionicNullifierClothingComponent : Component
{
    [DataField]
    public float Range = 5f;

    [ViewVariables]
    public EntityUid? Wearer;

    /// <summary>
    /// Whether the wearer's field is ours to take back. Someone born misaligned keeps their own.
    /// </summary>
    [ViewVariables]
    public bool GrantedNullifier;

    /// <summary>
    /// Whether the wearer's insulation is ours to take back once nothing else is holding it up.
    /// </summary>
    [ViewVariables]
    public bool GrantedInsulation;
}

/// <summary>
/// Sits on a psion for as long as they stand inside a null field. Every power attempt is cancelled.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PsionicallyNullifiedComponent : Component;

/// <summary>
/// Marks something that only exists, or only moves, because of a psionic power, so a null field can tell a
/// psionic fireball from a real bullet and a telekinetically hurled crate from a hand-thrown one.
/// </summary>
[RegisterComponent]
public sealed partial class PsionicManifestationComponent : Component
{
    /// <summary>
    /// True when the thing is made of the power and simply unravels. False when it is a real object being
    /// pushed by one, which just drops where it is.
    /// </summary>
    [DataField]
    public bool DeleteOnNullify = true;
}
