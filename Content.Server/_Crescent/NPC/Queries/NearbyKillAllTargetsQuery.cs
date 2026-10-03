using Content.Server._Crescent.NpcSquad;
using Content.Server.NPC.Queries.Queries;

namespace Content.Server._Crescent.NPC.Queries;

/// <summary>
/// Crescent: for a soldier under kill-all rules of engagement, every person nearby who is neither of its side nor
/// wearing an allied faction's ID. Empty otherwise. See <see cref="NpcKillAllComponent"/>.
/// </summary>
public sealed partial class NearbyKillAllTargetsQuery : UtilityQuery;
