using Content.Shared.Ghost;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Territory;

/// <summary>
/// A cryo spawner on a developed territory. Late-joining members of the holding faction can wake up here
/// instead of at their home station while it has power.
/// </summary>
[RegisterComponent]
public sealed partial class TerritorySpawnBeaconComponent : Component
{
    [DataField]
    public int MinLevel = 3;
}

/// <summary>
/// Only members of the faction holding the territory it stands on can open this.
/// </summary>
[RegisterComponent]
public sealed partial class TerritoryHolderOnlyComponent : Component;

public sealed class TerritoryHolderOnlySystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TerritoryHolderOnlyComponent, ActivatableUIOpenAttemptEvent>(OnUiOpenAttempt);
        SubscribeLocalEvent<TerritoryHolderOnlyComponent, BoundUserInterfaceMessageAttempt>(OnUiMessageAttempt);
    }

    private void OnUiOpenAttempt(Entity<TerritoryHolderOnlyComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || CanUse(ent, args.User))
            return;

        args.Cancel();
        _popup.PopupClient(Loc.GetString("territory-holder-only-denied"), ent, args.User);
    }

    private void OnUiMessageAttempt(Entity<TerritoryHolderOnlyComponent> ent, ref BoundUserInterfaceMessageAttempt args)
    {
        if (!args.Cancelled && !CanUse(ent, args.Actor))
            args.Cancel();
    }

    // admin ghosts get through, like FactionMachine
    private bool CanUse(EntityUid uid, EntityUid user)
    {
        if (HasComp<GhostComponent>(user))
            return true;

        return Transform(uid).GridUid is { } grid &&
               TryComp<PersistentPoiGridComponent>(grid, out var territory) &&
               PersistentTerritoryFactions.Holds(EntityManager, user, territory);
    }
}

[Serializable, NetSerializable]
public sealed class TerritoryDeployPoint
{
    public NetEntity Beacon;
    public string Name = string.Empty;
    public string Faction = string.Empty;
}

/// <summary>
/// Every spawn beacon that can take a late-joiner right now, for the lobby's late-join window.
/// </summary>
[Serializable, NetSerializable]
public sealed class TerritoryDeployPointsEvent : EntityEventArgs
{
    public List<TerritoryDeployPoint> Points = new();
}
