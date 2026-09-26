using Content.Server.NPC.Queries.Considerations;

namespace Content.Server._Crescent.NPC.Queries;

/// <summary>
/// Crescent: 1 if the NPC may shoot the target - it isn't on the NPC's side, and a squad member's orders
/// allow going after it - otherwise 0.
/// </summary>
public sealed partial class NpcSquadTargetCon : UtilityConsideration;
