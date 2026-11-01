using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Ghost;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.UserInterface;

namespace Content.Shared._Crescent.Factions;

/// <summary>
/// Resolves which faction a machine belongs to. See <see cref="FactionMachineComponent"/> for why machines
/// can't simply be identified by the grid they sit on.
/// </summary>
public sealed class FactionMachineSystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FactionMachineComponent, ActivatableUIOpenAttemptEvent>(OnUiOpenAttempt);
        SubscribeLocalEvent<FactionMachineComponent, BoundUserInterfaceMessageAttempt>(OnUiMessageAttempt);
    }

    private void OnUiOpenAttempt(Entity<FactionMachineComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (args.Cancelled || !ent.Comp.RestrictAccess)
            return;

        // Anything that gets this far as a ghost is an admin ghost — ordinary ghosts can't interact at all.
        // Admins have to be able to open a faction's machinery to see what it holds.
        if (CanUse(ent, args.User))
            return;

        args.Cancel();
        if (ent.Comp.DeniedPopup != null)
            _popup.PopupClient(Loc.GetString(ent.Comp.DeniedPopup), ent, args.User);
    }

    private void OnUiMessageAttempt(Entity<FactionMachineComponent> ent, ref BoundUserInterfaceMessageAttempt args)
    {
        // Revalidate every action, not just opening the window. This matters when a persistent territory changes
        // hands while a member of the old owner still has its console open.
        if (!args.Cancelled && ent.Comp.RestrictAccess && !CanUse(ent, args.Actor))
            args.Cancel();
    }

    private bool CanUse(Entity<FactionMachineComponent> ent, EntityUid user)
    {
        // Anything that reaches a machine as a ghost is an admin ghost; ordinary ghosts cannot interact.
        if (HasComp<GhostComponent>(user))
            return true;

        var machineFaction = GetFaction(ent);

        // Faction membership lives on the body, not the ID card, so read it off the user directly.
        // An independent's territory belongs to that one player, not to every IND.
        var userFaction = PersistentTerritoryFactions.IsIndependent(machineFaction)
            ? PersistentTerritoryFactions.GetTeam(EntityManager, user) ?? string.Empty
            : CompOrNull<HullrotFactionComponent>(user)?.Faction ?? string.Empty;

        // A restricted but currently unowned machine is locked, not public equipment for every unaligned character.
        return machineFaction.Length != 0 && userFaction == machineFaction;
    }

    /// <summary>
    /// The faction owning <paramref name="uid"/>, or empty if unaligned.
    /// </summary>
    public string GetFaction(EntityUid uid)
    {
        if (TryComp<FactionMachineComponent>(uid, out var machine) && (machine.Explicit || machine.Faction != ""))
            return Normalize(machine.Faction, machine.NormalizeFaction);

        // Not stamped, so fall back to the grid it's mounted on. This is the mapped-in station equipment case.
        if (Transform(uid).GridUid is { } grid && TryComp<IFFComponent>(grid, out var iff))
            return Normalize(iff.Faction, machine?.NormalizeFaction ?? true);

        return string.Empty;
    }

    /// <summary>
    /// Whether two machines belong to the same faction. Unaligned machines match only other unaligned ones.
    /// </summary>
    public bool SameFaction(EntityUid a, EntityUid b)
    {
        return GetFaction(a) == GetFaction(b);
    }

    /// <summary>
    /// Stamps an explicit faction onto a machine, overriding the grid fallback.
    /// </summary>
    public void SetFaction(EntityUid uid, string faction)
    {
        var comp = EnsureComp<FactionMachineComponent>(uid);
        comp.Faction = faction;
        comp.Explicit = true;
        Dirty(uid, comp);
    }

    /// <summary>
    /// TFCF member organizations resolve to a shared faction for machine ownership while retaining their
    /// own grids and run their own research servers, but syndicate hardware is shared across the umbrella, so
    /// a Gorlex server and a Cyberdawn microforge have to read as the same owner.
    /// </summary>
    private static readonly Dictionary<string, string> ParentFactions = new()
    {
        ["TAP"] = "TFSC", // Families
        ["IPM"] = "TFSC", // Interdyne
        ["SAW"] = "TFSC", // Saws
        ["GSC"] = "TFSC", // Gorlex
        ["CD"] = "TFSC", // Cyberdawn
    };

    /// <summary>
    /// Folds a faction ID the same way machine ownership does: "Neutral" becomes empty and TFCF member
    /// organizations become TFSC.
    /// </summary>
    public static string NormalizeFaction(string faction)
    {
        return Normalize(faction, true);
    }

    private static string Normalize(string faction, bool normalizeParent)
    {
        // "Neutral" is the IFF default for unaligned grids, so it means the same thing as no faction at all.
        if (faction == "Neutral")
            return string.Empty;

        return normalizeParent && ParentFactions.TryGetValue(faction, out var parent) ? parent : faction;
    }
}
