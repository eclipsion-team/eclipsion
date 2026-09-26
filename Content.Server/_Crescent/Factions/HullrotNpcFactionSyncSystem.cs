using Content.Shared._Crescent.HullrotFaction;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Factions;

/// <summary>
///     Crescent - mirrors a mob's <see cref="HullrotFactionComponent"/> onto its NPC faction membership, so NPCs
///     that reason about factions can see which side a player is actually on.
/// </summary>
/// <remarks>
///     <para>
///     Player mobs ship as NanoTrasen (BaseMobSpeciesOrganic) and the job specials only ever wrote
///     HullrotFaction, so nothing ever put a player into DSM/NCWL/TFSC/SHI/IND. Those five ids are exactly what
///     an anti-boarder PD turret lists as hostile, so <c>GetNearbyHostiles</c> always came back empty and every
///     PD turret on every map sat spinning on its idle branch instead of engaging anyone - the turrets could
///     only ever see each other and the faction mechs.
///     </para>
///     <para>
///     NanoTrasen is deliberately left in place next to the added id rather than replaced: it is what makes a
///     player a target for pirates, xenos and the rest of the generic NPC roster, and dropping it would quietly
///     disarm all of them.
///     </para>
/// </remarks>
public sealed class HullrotNpcFactionSyncSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HullrotFactionComponent, ComponentStartup>(OnFactionStartup);
        // ComponentShutdown on this component is already claimed by OverwatchSystem, and the engine allows exactly
        // one subscriber per component/component-event pair - taking it here is a hard crash on server start.
        SubscribeLocalEvent<HullrotFactionComponent, ComponentRemove>(OnFactionRemove);
    }

    private void OnFactionStartup(Entity<HullrotFactionComponent> ent, ref ComponentStartup args)
    {
        Sync(ent.Owner, ent.Comp);
    }

    private void OnFactionRemove(Entity<HullrotFactionComponent> ent, ref ComponentRemove args)
    {
        // Startup normally creates the bookkeeping component, but a component can also be removed before it
        // ever starts (or while its entity is terminating). Never add a new component during either removal path.
        if (!TryComp<HullrotNpcFactionComponent>(ent, out var applied) || applied.Applied is not { } old)
            return;

        _npcFaction.RemoveFaction(ent.Owner, old);
        applied.Applied = null;
    }

    /// <summary>
    ///     Re-reads the mob's Hullrot faction and moves its NPC faction membership to match.
    /// </summary>
    /// <remarks>
    ///     Call this after writing <see cref="HullrotFactionComponent.Faction"/> by hand.
    ///     The component carries no change event of its own, so a recruitment that only assigns the field would
    ///     otherwise leave every turret in the sector still working off the mob's previous allegiance.
    /// </remarks>
    public void Sync(EntityUid uid, HullrotFactionComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        // Trimmed because at least one job prototype writes "ATH " with a trailing space, and an id only has to
        // miss by a character to silently resolve to nothing.
        var faction = comp.Faction.Trim();

        // Only ids that exist as an npcFaction get applied. Several Hullrot factions (ATH, GS, TAP, TSP)
        // have no NPC counterpart at all, and AddFaction logs an error for every call with an unknown id.
        Apply(uid, _proto.HasIndex<NpcFactionPrototype>(faction) ? faction : null);
    }

    private void Apply(EntityUid uid, string? faction)
    {
        var applied = EnsureComp<HullrotNpcFactionComponent>(uid);

        if (applied.Applied == faction)
            return;

        if (applied.Applied is { } old)
            _npcFaction.RemoveFaction(uid, old);

        if (faction != null)
            _npcFaction.AddFaction(uid, faction);

        applied.Applied = faction;
    }
}
