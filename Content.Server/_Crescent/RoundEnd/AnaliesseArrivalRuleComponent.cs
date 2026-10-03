using Content.Server.Maps;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.RoundEnd;

/// <summary>
/// The CMM finale: brings the Analiesse back into the sector as a live, crewable station. Her gameMap is loaded,
/// initialized as a station (so her jobs open for latejoin) and jumped in beside the faction's seat of power.
///
/// Started by the CMM directive terminal when MissionAnaliesseKey is turned in (see
/// <see cref="Content.Shared._Crescent.RoundEnd.FactionMissionPrototype.RewardGameRules"/>), or by an admin.
/// </summary>
[RegisterComponent, Access(typeof(AnaliesseArrivalRuleSystem))]
public sealed partial class AnaliesseArrivalRuleComponent : Component
{
    /// <summary>The gameMap to bring in. Its station entry must share the gameMap's id.</summary>
    [DataField]
    public ProtoId<GameMapPrototype> GameMap = "Analiesse";

    /// <summary>
    /// She arrives near the grid carrying a FactionStation of this faction (Gliess Santo for the CMM). Falls back
    /// to the default map origin when no such station is in play.
    /// </summary>
    [DataField]
    public string NearFaction = "CMM";

    /// <summary>Ring around the anchor point she drops out of FTL somewhere inside, so she never lands on it.</summary>
    [DataField]
    public float MinDistance = 600f;

    [DataField]
    public float MaxDistance = 900f;

    [DataField]
    public Color IffColor = Color.FromHex("#218c74");

    [DataField]
    public string IffFaction = "CMM";

    /// <summary>How long she spends in hyperspace before arriving, in seconds.</summary>
    [DataField]
    public float HyperspaceTime = 30f;

    /// <summary>The arrived grid, once she exists. One Analiesse per round: a second start does nothing.</summary>
    [ViewVariables]
    public EntityUid? GridUid;

    /// <summary>The map she is loaded onto before the jump. Deleted once she has left it.</summary>
    [ViewVariables]
    public EntityUid? HoldingMap;
}
