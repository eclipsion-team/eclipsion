using Content.Shared._Crescent.Factions;
using Content.Shared.Doors;
using Content.Shared.Doors.Systems;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Robust.Shared.GameStates;

namespace Content.Shared._Crescent.Territory;

[RegisterComponent, NetworkedComponent]
public sealed partial class TerritoryAccessComponent : Component;

/// <summary>
/// A soldier a territory put up, guarding it or sent along with its holder. Never saved with the territory.
/// </summary>
[RegisterComponent]
public sealed partial class TerritoryGarrisonSoldierComponent : Component
{
    [DataField]
    public string Faction = string.Empty;

    [DataField]
    public string RegionId = string.Empty;

    [DataField]
    public bool Escort;
}

public sealed class TerritoryAccessSystem : EntitySystem
{
    [Dependency] private readonly FactionMachineSystem _factionMachines = default!;
    [Dependency] private readonly SharedDoorSystem _doors = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TerritoryAccessComponent, BeforeDoorOpenedEvent>(OnBeforeDoorOpened);
        SubscribeLocalEvent<TerritoryAccessComponent, ExaminedEvent>(OnExamined);
    }

    private void OnBeforeDoorOpened(EntityUid uid, TerritoryAccessComponent component, BeforeDoorOpenedEvent args)
    {
        if (args.Cancelled || args.User is not { } user || CanPass(uid, user))
            return;

        args.Cancel();
        _doors.Deny(uid, user: user, predicted: true);
    }

    private void OnExamined(Entity<TerritoryAccessComponent> ent, ref ExaminedEvent args)
    {
        var holder = _factionMachines.GetFaction(ent);
        args.PushMarkup(holder.Length == 0
            ? Loc.GetString("territory-access-examine-open")
            : Loc.GetString("territory-access-examine-held", ("faction", FactionDisplay.Abbreviation(holder))));
    }

    public bool CanPass(EntityUid door, EntityUid user)
    {
        if (HasComp<GhostComponent>(user))
            return true;

        var holder = _factionMachines.GetFaction(door);
        return holder.Length == 0 ||
               string.Equals(PersistentTerritoryFactions.GetTeam(EntityManager, user), holder, StringComparison.Ordinal) ||
               TryComp<TerritoryGarrisonSoldierComponent>(user, out var soldier) &&
               string.Equals(soldier.Faction, holder, StringComparison.Ordinal);
    }
}
