using Content.Server.Access.Systems;
using Content.Server.Jobs;
using Content.Server._Crescent.Diplomacy;
using Content.Shared.Access.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.NPC.Components;
using Content.Shared.Roles;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.Factions;
using Content.Shared._Crescent.HullrotFaction;

namespace Content.Server._Crescent.Factions;

/// <summary>
///     What a faction's guard - an anti-boarder gun, a soldier NPC - makes of the credential someone is wearing.
/// </summary>
public enum FactionCredentialStanding : byte
{
    /// <summary>No faction card in the ID slot: lost, taken off, or never had one.</summary>
    Unknown,

    /// <summary>The card of a faction the guard's side is neither at war nor allied with.</summary>
    Neutral,

    /// <summary>
    ///     The card of a faction at war with the guard's side, or of one with no seat at the diplomacy table to
    ///     vouch for its bearer.
    /// </summary>
    Hostile,

    /// <summary>The card of the guard's own faction or of an ally.</summary>
    Allied,
}

/// <summary>
///     Assigns faction identity to preset ID cards and resolves the credential worn in a mob's ID slot.
/// </summary>
public sealed partial class FactionIdCardSystem : EntitySystem
{
    [Dependency] private readonly IdCardSystem _idCards = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly RatDiplomacySystem _diplomacy = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DidEquipEvent>(OnDidEquip);
    }

    private void OnDidEquip(DidEquipEvent args)
    {
        if (args.Slot != "id" ||
            !_idCards.TryGetIdCard(args.Equipment, out var idCard) ||
            !TryComp<FactionIdCardComponent>(idCard, out var factionId))
        {
            return;
        }

        TrackCredential(args.Equipee, idCard, factionId);
    }

    private void TrackCredential(
        EntityUid memberUid,
        Entity<IdCardComponent> idCard,
        FactionIdCardComponent factionId)
    {
        if (!TryComp<HullrotFactionComponent>(memberUid, out var member) ||
            factionId.Faction != member.Faction ||
            idCard.Comp.FullName != MetaData(memberUid).EntityName)
        {
            return;
        }

        var tracker = EnsureComp<FactionCredentialTrackerComponent>(memberUid);
        if (tracker.Faction != factionId.Faction)
        {
            foreach (var oldCard in tracker.Cards)
                ClearFaction(oldCard, tracker.Faction);

            tracker.Cards.Clear();
            tracker.Faction = factionId.Faction;
        }

        tracker.Cards.Add(idCard);
    }

    /// <summary>
    ///     Copies the faction granted by a job onto an ID. Returns false for jobs without a faction grant.
    /// </summary>
    public bool SetFactionFromJob(EntityUid id, JobPrototype job)
    {
        foreach (var special in job.Special)
        {
            if (special is not AddComponentSpecial add)
                continue;

            foreach (var entry in add.Components.Values)
            {
                if (entry.Component is not HullrotFactionComponent faction ||
                    string.IsNullOrWhiteSpace(faction.Faction))
                {
                    continue;
                }

                SetFaction(id, faction.Faction);
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets the faction credential advertised by an ID card.</summary>
    public void SetFaction(EntityUid id, string faction)
    {
        var component = EnsureComp<FactionIdCardComponent>(id);
        component.Faction = faction.Trim();
        Dirty(id, component);
    }

    /// <summary>
    /// Removes a faction credential from an ID. If <paramref name="expectedFaction"/> is supplied, a credential
    /// belonging to another faction is left untouched.
    /// </summary>
    public bool ClearFaction(EntityUid id, string? expectedFaction = null)
    {
        if (!TryComp<FactionIdCardComponent>(id, out var component) ||
            expectedFaction != null && component.Faction != expectedFaction)
        {
            return false;
        }

        RemComp<FactionIdCardComponent>(id);
        return true;
    }

    /// <summary>
    /// Gets the actual card contained by the item equipped in <paramref name="wearer"/>'s ID slot.
    /// </summary>
    public bool TryGetWornIdCard(EntityUid wearer, out Entity<IdCardComponent> idCard)
    {
        idCard = default;

        return _inventory.TryGetSlotEntity(wearer, "id", out var idItem) &&
               _idCards.TryGetIdCard(idItem.Value, out idCard);
    }

    /// <summary>
    ///     Gets the faction from the ID actually equipped in <paramref name="wearer"/>'s ID slot. A card merely
    ///     held in an active hand is intentionally not accepted.
    /// </summary>
    public bool TryGetWornFaction(EntityUid wearer, out string faction)
    {
        faction = string.Empty;

        if (!TryGetWornIdCard(wearer, out var id) ||
            !TryComp<FactionIdCardComponent>(id, out var factionId) ||
            string.IsNullOrWhiteSpace(factionId.Faction))
        {
            return false;
        }

        TrackCredential(wearer, id, factionId);
        faction = factionId.Faction;
        return true;
    }

    /// <summary>
    ///     Reads the card in <paramref name="wearer"/>'s ID slot against the live diplomacy of every NPC faction
    ///     <paramref name="reader"/> belongs to.
    /// </summary>
    /// <remarks>
    ///     A card from a faction with no seat at the diplomacy table - the spacers' IND - has no treaty behind it and
    ///     reads as hostile, exactly as if its faction were at war. A reader that counts that faction among its own,
    ///     as Gliess's guns count IND, still reads it as allied.
    /// </remarks>
    public FactionCredentialStanding ReadCredential(EntityUid reader, EntityUid wearer)
    {
        if (!TryGetWornFaction(wearer, out var faction))
            return FactionCredentialStanding.Unknown;

        var atWar = false;
        if (TryComp<NpcFactionMemberComponent>(reader, out var member))
        {
            foreach (var own in member.Factions)
            {
                var relation = _diplomacy.GetRelation(own, faction);
                if (relation == FactionRelation.Alliance)
                    return FactionCredentialStanding.Allied;

                atWar |= relation == FactionRelation.War;
            }
        }

        return atWar || !_diplomacy.IsDiplomaticFaction(faction)
            ? FactionCredentialStanding.Hostile
            : FactionCredentialStanding.Neutral;
    }
}
