using Content.Server.NPC.Queries.Queries;

namespace Content.Server._Crescent.NPC.Queries;

/// <summary>
/// Crescent: nearby working mechs with an enemy at the controls, picked out by scanning the pilot's side and the
/// faction ID they wear. See <see cref="NpcMechTargetingSystem"/>.
/// </summary>
public sealed partial class NearbyHostileMechsQuery : UtilityQuery;
