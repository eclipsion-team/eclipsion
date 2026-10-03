using Content.Server.NPC.Queries.Considerations;

namespace Content.Server._Crescent.NPC.Queries;

/// <summary>
/// Crescent: TargetIsAliveCon that also passes a mech still in the fight, which has no mob state of its own to
/// be alive with. See <see cref="NpcMechTargetingSystem.IsActiveTarget"/>.
/// </summary>
public sealed partial class NpcTargetActiveCon : UtilityConsideration;
