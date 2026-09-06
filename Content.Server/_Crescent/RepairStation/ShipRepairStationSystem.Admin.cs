using Content.Shared.Administration;
using Content.Shared._Crescent.RepairStation;
using Content.Shared.Database;
using Content.Shared.UserInterface;

namespace Content.Server._Crescent.RepairStation;

/// <summary>
/// The parts of the slip that only exist on the administrative override console: who is allowed to
/// touch it at all, and its one extra power - filing a structural blueprint of a hull that never had
/// one, which is every station and every ship that was mapped in rather than bought.
/// </summary>
public sealed partial class ShipRepairStationSystem
{
    private void InitializeAdmin()
    {
        SubscribeLocalEvent<ShipRepairStationComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<ShipRepairStationComponent, ShipRepairSnapshotMessage>(OnSnapshot);
    }

    /// <summary>
    /// Whether this player may work the console. Everything the window can ask for is checked against
    /// this, not just the opening of it: a bound interface message is a packet the client sends, so a
    /// gate on the window alone is no gate at all.
    /// </summary>
    public bool CanUse(Entity<ShipRepairStationComponent> station, EntityUid actor)
    {
        return !station.Comp.AdminOnly || _admin.HasAdminFlag(actor, AdminFlags.Admin);
    }

    private void OnOpenAttempt(Entity<ShipRepairStationComponent> station, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || CanUse(station, args.User))
            return;

        args.Cancel();
        Deny(station, args.User, "ship-repair-station-popup-admin-only");
    }

    /// <summary>
    /// Files the hull in front of the slip exactly as it stands. Whatever is on the deck at this moment
    /// becomes what the slip puts back after the next hit, so it is worth taking on a sound hull rather
    /// than on a wreck - a blueprint filed off a wreck restores a wreck.
    /// </summary>
    private void OnSnapshot(Entity<ShipRepairStationComponent> station, ref ShipRepairSnapshotMessage args)
    {
        if (!CanUse(station, args.Actor))
        {
            Deny(station, args.Actor, "ship-repair-station-popup-admin-only");
            return;
        }

        if (!station.Comp.AllowSnapshot)
            return;

        var serviceable = GetServiceableGrids(station);
        if (GetSelection(station, serviceable) is not { } ship)
        {
            Deny(station, args.Actor, "ship-repair-station-popup-no-ship");
            return;
        }

        // Rewriting the file out from under a running job would have the slip finish rebuilding a hull
        // against a blueprint taken halfway through rebuilding it.
        if (station.Comp.Target == ship || IsShipBusyElsewhere(station, ship))
        {
            Deny(station, args.Actor, "ship-repair-station-popup-busy");
            return;
        }

        _shipRepair.GenerateRepairData(ship);

        _adminLogger.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(args.Actor):actor} filed a repair blueprint of {ToPrettyString(ship):grid} from {ToPrettyString(station.Owner):console}");

        _audio.PlayPvs(station.Comp.ConfirmSound, station.Owner);
        _popup.PopupEntity(Loc.GetString("ship-repair-station-popup-snapshot", ("ship", Name(ship))), station.Owner, args.Actor);
        PushUi(station);
    }
}
