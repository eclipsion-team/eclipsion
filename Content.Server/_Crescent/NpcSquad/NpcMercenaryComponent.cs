namespace Content.Server._Crescent.NpcSquad;

/// <summary>
/// Crescent: a hired gun. Goes for nobody of its own accord, only for whoever its leader points out and whoever
/// attacks the leader or the leader's mercenaries.
/// </summary>
[RegisterComponent, Access(typeof(NpcMercenarySystem))]
public sealed partial class NpcMercenaryComponent : Component
{
    /// <summary>Its only NPC faction. Factions worn clothing adds are taken back off.</summary>
    [DataField]
    public string Faction = "Mercenary";

    [DataField]
    public TimeSpan RetaliationTime = TimeSpan.FromMinutes(2);
}

/// <summary>Crescent: a player who has hired mercenaries, so an attack on them is answered.</summary>
[RegisterComponent, Access(typeof(NpcMercenarySystem))]
public sealed partial class NpcMercenaryEmployerComponent : Component;
