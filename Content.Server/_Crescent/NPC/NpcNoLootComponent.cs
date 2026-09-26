using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.NPC;

/// <summary>
/// Crescent: nothing comes off this NPC. It can't be stripped, whatever leaves its hands or inventory for
/// the floor - knocked out of its hands, dropped as it goes down - is gone, and when it dies it crumbles to
/// dust along with everything it was carrying.
/// </summary>
[RegisterComponent, Access(typeof(NpcNoLootSystem))]
public sealed partial class NpcNoLootComponent : Component
{
    /// <summary>
    /// What is left where it died.
    /// </summary>
    [DataField]
    public EntProtoId? Remains = "Ash";

    [DataField]
    public LocId DustMessage = "npc-no-loot-dust";
}
