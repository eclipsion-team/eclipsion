using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.Territory;
using Content.Shared.Ghost;
using Content.Shared.Popups;
using Content.Shared.UserInterface;

namespace Content.Shared._Crescent.FactionPortal;

public abstract class SharedFactionPortalSystem : EntitySystem
{
    [Dependency] protected readonly FactionMachineSystem FactionMachine = default!;
    [Dependency] protected readonly SharedPopupSystem Popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FactionPortalComponent, ActivatableUIOpenAttemptEvent>(OnUiOpenAttempt);
    }

    private void OnUiOpenAttempt(Entity<FactionPortalComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || CanUse(ent, args.User))
            return;

        args.Cancel();
        Popup.PopupClient(Loc.GetString("faction-portal-denied"), ent, args.User);
    }

    /// <summary>
    /// The portal's faction with TFCF member organizations folded into TFSC, or empty if unowned. Territory
    /// portals belong to whoever holds the ground.
    /// </summary>
    public string GetFaction(Entity<FactionPortalComponent?> portal)
    {
        if (!Resolve(portal, ref portal.Comp, false))
            return string.Empty;

        if (portal.Comp.MinTerritoryLevel > 0)
        {
            return Transform(portal).GridUid is { } grid && TryComp<PersistentPoiGridComponent>(grid, out var territory)
                ? FactionMachineSystem.NormalizeFaction(territory.Owner ?? string.Empty)
                : string.Empty;
        }

        return FactionMachineSystem.NormalizeFaction(FactionMachine.GetFaction(portal));
    }

    public bool CanUse(EntityUid portal, EntityUid user)
    {
        // only admin ghosts get this far
        if (HasComp<GhostComponent>(user))
            return true;

        var faction = GetFaction(portal);
        if (faction.Length == 0)
            return false;

        var team = PersistentTerritoryFactions.GetTeam(EntityManager, user);
        return team != null && FactionMachineSystem.NormalizeFaction(team) == faction;
    }
}
