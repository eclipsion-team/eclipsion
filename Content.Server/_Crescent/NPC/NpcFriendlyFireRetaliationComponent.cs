namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: a faction soldier that is shot or hit by someone on its own side fights back - until it has put
/// them down. Once the attacker is critical (or dead) the grudge is dropped and they are a friend again, until
/// the next time they hurt it.
/// </summary>
/// <remarks>
/// The grudge goes into the NPC's FactionException hostiles, which is what both the target queries and
/// NpcIff read, so while it lasts the NPC picks the attacker as a target and its rounds stop passing through
/// them. Other soldier AI never start one: a friendly NPC's stray splash is an accident, not a betrayal.
/// </remarks>
[RegisterComponent, Access(typeof(NpcFriendlyFireRetaliationSystem))]
public sealed partial class NpcFriendlyFireRetaliationComponent : Component
{
    /// <summary>
    /// How long after their last hit an attacker it never managed to put down is forgiven anyway, so someone
    /// who fired once and ran isn't hunted for the rest of the round. Null never forgives.
    /// </summary>
    [DataField]
    public TimeSpan? ForgetAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Everyone on its own side it is currently fighting, and when they last hurt it.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> Grudges = new();
}
