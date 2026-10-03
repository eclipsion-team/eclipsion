namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: a faction soldier's rules of engagement. Under kill-all it goes for anyone who is neither of its own
/// side nor wearing an allied faction's ID - a third party nobody declared war on, an unaligned boarder, someone
/// who took their ID off - instead of only the factions its NPC faction lists as hostile.
/// </summary>
/// <remarks>
/// Only people count: the station's pets and vermin are nobody's boarders. While the soldier follows a squad, the
/// squad leader's setting in the squad window is used instead of this one.
/// </remarks>
[RegisterComponent, Access(typeof(NpcSquadSystem))]
public sealed partial class NpcKillAllComponent : Component
{
    [DataField]
    public bool Enabled = true;
}
