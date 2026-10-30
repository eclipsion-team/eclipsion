using System.Diagnostics.CodeAnalysis;
using Content.Server.Administration;
using Content.Shared._Crescent.HardsuitInjection;
using Content.Shared.Actions;
using Content.Shared.Administration.Logs;
using Content.Shared.Alert;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared._Goobstation.Chemistry.Hypospray;
using Content.Shared.Clothing;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Forensics;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Player;

namespace Content.Server._Crescent.HardsuitInjection;

public sealed class HardsuitInjectorSystem : EntitySystem
{
    private static readonly SoundSpecifier SuitInjectSound = new SoundPathSpecifier("/Audio/Items/hypospray.ogg");

    [Dependency] private readonly ActionContainerSystem _actionContainer = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly QuickDialogSystem _quickDialog = default!;
    [Dependency] private readonly ReactiveSystem _reactive = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HardsuitInjectorComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<HardsuitInjectorComponent, GetItemActionsEvent>(OnGetActions);
        SubscribeLocalEvent<HardsuitInjectorComponent, ItemSlotInsertEvent>(OnItemInserted);
        SubscribeLocalEvent<HardsuitInjectorComponent, ItemSlotEjectEvent>(OnItemEjected);
        SubscribeLocalEvent<HardsuitInjectorComponent, HardsuitInjectActionEvent>(OnInjectAction);
        SubscribeLocalEvent<HardsuitInjectorComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<HardsuitInjectorComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<HardsuitInjectorComponent, GotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<HardsuitInjectorComponent, GotUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<HardsuitInjectorComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<HardsuitInjectorComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnEquipped(Entity<HardsuitInjectorComponent> ent, ref GotEquippedEvent args)
    {
        if ((args.SlotFlags & SlotFlags.OUTERCLOTHING) == 0)
            return;

        UpdateMissingAlert(ent, args.Equipee);
    }

    private void OnUnequipped(Entity<HardsuitInjectorComponent> ent, ref GotUnequippedEvent args)
    {
        if ((args.SlotFlags & SlotFlags.OUTERCLOTHING) == 0)
            return;

        _alerts.ClearAlert(args.Equipee, ent.Comp.MissingInjectorAlert);
    }

    private void OnContainerChanged<T>(Entity<HardsuitInjectorComponent> ent, ref T args) where T : ContainerModifiedMessage
    {
        if (!IsInjectorSlot(args.Container.ID) || !TryGetWearer(ent.Owner, out var wearer))
            return;

        UpdateMissingAlert(ent, wearer);
    }

    private void UpdateMissingAlert(Entity<HardsuitInjectorComponent> ent, EntityUid wearer)
    {
        if (HasUsableInjector(ent.Owner, HardsuitInjectorComponent.SlotOneId) &&
            HasUsableInjector(ent.Owner, HardsuitInjectorComponent.SlotTwoId))
        {
            _alerts.ClearAlert(wearer, ent.Comp.MissingInjectorAlert);
            return;
        }

        _alerts.ShowAlert(wearer, ent.Comp.MissingInjectorAlert);
    }

    private bool HasUsableInjector(EntityUid suit, string slotId)
    {
        return TryGetInjectorSource(suit, slotId, out var source) &&
               TryGetSourceSolution(source, out _, out var solution, out _) &&
               solution.Volume > 0;
    }

    private void OnMapInit(Entity<HardsuitInjectorComponent> ent, ref MapInitEvent args)
    {
        EnsureActions(ent);
        UpdateActionIcon(ent, HardsuitInjectorComponent.SlotOneId);
        UpdateActionIcon(ent, HardsuitInjectorComponent.SlotTwoId);
    }

    private void EnsureActions(Entity<HardsuitInjectorComponent> ent)
    {
        _actionContainer.EnsureAction(ent.Owner, ref ent.Comp.SlotOneActionEntity, ent.Comp.SlotOneAction);
        _actionContainer.EnsureAction(ent.Owner, ref ent.Comp.SlotTwoActionEntity, ent.Comp.SlotTwoAction);
        Dirty(ent);
    }

    private void OnGetActions(Entity<HardsuitInjectorComponent> ent, ref GetItemActionsEvent args)
    {
        if (args.SlotFlags == null || (args.SlotFlags.Value & SlotFlags.OUTERCLOTHING) == 0)
            return;

        if (TryGetInjectorSource(ent.Owner, HardsuitInjectorComponent.SlotOneId, out _))
            args.AddAction(ref ent.Comp.SlotOneActionEntity, ent.Comp.SlotOneAction);

        if (TryGetInjectorSource(ent.Owner, HardsuitInjectorComponent.SlotTwoId, out _))
            args.AddAction(ref ent.Comp.SlotTwoActionEntity, ent.Comp.SlotTwoAction);
    }

    private void OnItemInserted(Entity<HardsuitInjectorComponent> ent, ref ItemSlotInsertEvent args)
    {
        var slotId = args.Slot.ID;
        if (!IsInjectorSlot(slotId))
            return;

        EnsureActions(ent);
        UpdateActionIcon(ent, slotId!);

        if (!TryGetWearer(ent.Owner, out var wearer))
            return;

        var action = GetActionEntity(ent.Comp, slotId!);
        if (action != null)
            _actions.AddAction(wearer, action.Value, ent.Owner);
    }

    private void OnItemEjected(Entity<HardsuitInjectorComponent> ent, ref ItemSlotEjectEvent args)
    {
        var slotId = args.Slot.ID;
        if (!IsInjectorSlot(slotId))
            return;

        var action = GetActionEntity(ent.Comp, slotId!);
        if (action != null && TryGetWearer(ent.Owner, out var wearer))
            _actions.RemoveProvidedAction(wearer, ent.Owner, action.Value);

        if (action != null)
            _actions.SetEntityIcon(action.Value, null);
    }

    private void OnInjectAction(Entity<HardsuitInjectorComponent> ent, ref HardsuitInjectActionEvent args)
    {
        if (args.Handled || !IsInjectorSlot(args.Slot))
            return;

        args.Handled = true;

        if (!TryGetWearer(ent.Owner, out var wearer) || wearer != args.Performer)
            return;

        TryInjectSlot(ent, args.Slot, wearer, false);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Critical || args.OldMobState == MobState.Critical)
            return;

        if (!_inventory.TryGetSlotEntity(args.Target, "outerClothing", out var suit) ||
            !TryComp<HardsuitInjectorComponent>(suit, out var injector) ||
            !injector.AutoInjectOnCritical)
        {
            return;
        }

        var ent = new Entity<HardsuitInjectorComponent>(suit.Value, injector);
        // Only use one pen per critical transition. Two emergency pens at once would exceed
        // epinephrine's safe threshold; the second slot acts as a backup once the first is spent.
        if (!TryInjectSlot(ent, HardsuitInjectorComponent.SlotOneId, args.Target, true))
            TryInjectSlot(ent, HardsuitInjectorComponent.SlotTwoId, args.Target, true);
    }

    private bool TryInjectSlot(Entity<HardsuitInjectorComponent> suit, string slotId, EntityUid wearer, bool automatic)
    {
        if (!TryGetInjectorSource(suit.Owner, slotId, out var source) ||
            !TryGetSourceSolution(source, out var sourceSolutionEntity, out var sourceSolution, out var injectSound) ||
            sourceSolution.Volume <= 0)
        {
            if (!automatic)
                _popup.PopupEntity(Loc.GetString("hardsuit-injector-empty"), suit.Owner, wearer, PopupType.SmallCaution);
            return false;
        }

        if (!_solution.TryGetInjectableSolution(wearer, out var targetSolutionEntity, out var targetSolution))
        {
            if (!automatic)
                _popup.PopupEntity(Loc.GetString("hardsuit-injector-cannot-inject"), suit.Owner, wearer, PopupType.SmallCaution);
            return false;
        }

        var configuredAmount = GetTransferAmount(suit.Comp, slotId);
        var transferAmount = FixedPoint2.Min(configuredAmount, sourceSolution.Volume, targetSolution.AvailableVolume);
        if (transferAmount <= 0)
        {
            if (!automatic)
                _popup.PopupEntity(Loc.GetString("hardsuit-injector-cannot-inject"), suit.Owner, wearer, PopupType.SmallCaution);
            return false;
        }

        var removedSolution = _solution.SplitSolution(sourceSolutionEntity.Value, transferAmount);
        _reactive.DoEntityReaction(wearer, removedSolution, ReactionMethod.Injection);
        if (!_solution.TryAddSolution(targetSolutionEntity.Value, removedSolution))
            return false;

        _audio.PlayPvs(injectSound, wearer);
        UpdateMissingAlert(suit, wearer);

        var dna = new TransferDnaEvent { Donor = wearer, Recipient = source };
        RaiseLocalEvent(wearer, ref dna);

        if (HasComp<HyposprayComponent>(source))
        {
            var afterInject = new AfterHyposprayInjectsEvent { User = wearer, Target = wearer };
            RaiseLocalEvent(source, ref afterInject);
        }

        _popup.PopupEntity(
            Loc.GetString(automatic ? "hardsuit-injector-auto-injected" : "hardsuit-injector-injected",
                ("source", source),
                ("amount", transferAmount)),
            wearer,
            wearer,
            automatic ? PopupType.LargeCaution : PopupType.Small);

        _adminLogger.Add(
            LogType.ForceFeed,
            $"{ToPrettyString(suit.Owner):suit} injected {ToPrettyString(wearer):target} with " +
            $"{SharedSolutionContainerSystem.ToPrettyString(removedSolution):removedSolution} using {ToPrettyString(source):source}");

        return true;
    }

    private void OnGetVerbs(Entity<HardsuitInjectorComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || !TryComp<ActorComponent>(args.User, out var actor))
            return;

        AddDoseVerb(ent, HardsuitInjectorComponent.SlotOneId, args, actor);
        AddDoseVerb(ent, HardsuitInjectorComponent.SlotTwoId, args, actor);
    }

    private void AddDoseVerb(
        Entity<HardsuitInjectorComponent> ent,
        string slotId,
        GetVerbsEvent<AlternativeVerb> args,
        ActorComponent actor)
    {
        if (!TryGetInjectorSource(ent.Owner, slotId, out var source))
            return;

        var currentAmount = GetTransferAmount(ent.Comp, slotId);
        var slotNumber = slotId == HardsuitInjectorComponent.SlotOneId ? 1 : 2;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("hardsuit-injector-set-dose-verb",
                ("slot", slotNumber),
                ("amount", currentAmount)),
            Category = VerbCategory.SetTransferAmount,
            IconEntity = GetNetEntity(source),
            Act = () => OpenDoseDialog(ent, slotId, slotNumber, actor)
        });
    }

    private void OpenDoseDialog(
        Entity<HardsuitInjectorComponent> ent,
        string slotId,
        int slotNumber,
        ActorComponent actor)
    {
        var minimum = ent.Comp.MinimumTransferAmount.Int();
        var maximum = ent.Comp.MaximumTransferAmount.Int();
        _quickDialog.OpenDialog(
            actor.PlayerSession,
            Loc.GetString("hardsuit-injector-dose-dialog-title", ("slot", slotNumber)),
            Loc.GetString("hardsuit-injector-dose-dialog-prompt", ("min", minimum), ("max", maximum)),
            (int amount) =>
            {
                if (Deleted(ent.Owner) ||
                    !TryComp<HardsuitInjectorComponent>(ent.Owner, out var injector) ||
                    amount < minimum ||
                    amount > maximum ||
                    !TryGetInjectorSource(ent.Owner, slotId, out _))
                {
                    _popup.PopupEntity(
                        Loc.GetString("hardsuit-injector-invalid-dose", ("min", minimum), ("max", maximum)),
                        actor.PlayerSession.AttachedEntity ?? ent.Owner,
                        actor.PlayerSession,
                        PopupType.SmallCaution);
                    return;
                }

                SetTransferAmount(injector, slotId, FixedPoint2.New(amount));
                Dirty(ent.Owner, injector);
                _popup.PopupEntity(
                    Loc.GetString("hardsuit-injector-dose-set", ("slot", slotNumber), ("amount", amount)),
                    ent.Owner,
                    actor.PlayerSession,
                    PopupType.Small);
            });
    }

    private void OnExamined(Entity<HardsuitInjectorComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        using (args.PushGroup(nameof(HardsuitInjectorComponent)))
        {
            AddSlotExamine(ent, HardsuitInjectorComponent.SlotOneId, 1, args);
            AddSlotExamine(ent, HardsuitInjectorComponent.SlotTwoId, 2, args);
        }
    }

    private void AddSlotExamine(Entity<HardsuitInjectorComponent> ent, string slotId, int slotNumber, ExaminedEvent args)
    {
        var amount = GetTransferAmount(ent.Comp, slotId);
        if (TryGetInjectorSource(ent.Owner, slotId, out var source))
        {
            args.PushMarkup(Loc.GetString("hardsuit-injector-examine-loaded",
                ("slot", slotNumber),
                ("source", source),
                ("amount", amount)));
            return;
        }

        args.PushMarkup(Loc.GetString("hardsuit-injector-examine-empty", ("slot", slotNumber)));
    }

    private void UpdateActionIcon(Entity<HardsuitInjectorComponent> ent, string slotId)
    {
        var action = GetActionEntity(ent.Comp, slotId);
        if (action == null)
            return;

        _actions.SetEntityIcon(action.Value, TryGetInjectorSource(ent.Owner, slotId, out var source) ? source : null);
    }

    private bool TryGetInjectorSource(EntityUid suit, string slotId, out EntityUid source)
    {
        source = EntityUid.Invalid;
        if (!_itemSlots.TryGetSlot(suit, slotId, out var slot) ||
            slot.Item is not { } item ||
            (!HasComp<HardsuitInjectableComponent>(item) && !HasComp<HyposprayComponent>(item)))
        {
            return false;
        }

        source = item;
        return true;
    }

    private bool TryGetSourceSolution(
        EntityUid source,
        [NotNullWhen(true)] out Entity<SolutionComponent>? solutionEntity,
        [NotNullWhen(true)] out Solution? solution,
        out SoundSpecifier injectSound)
    {
        if (TryComp<HyposprayComponent>(source, out var hypospray))
        {
            injectSound = hypospray.InjectSound;
            return _solution.TryGetSolution(source, hypospray.SolutionName, out solutionEntity, out solution);
        }

        injectSound = SuitInjectSound;
        return _solution.TryGetRefillableSolution(source, out solutionEntity, out solution);
    }

    private bool TryGetWearer(EntityUid suit, out EntityUid wearer)
    {
        wearer = Transform(suit).ParentUid;
        return wearer.IsValid() &&
               _inventory.TryGetSlotEntity(wearer, "outerClothing", out var equipped) &&
               equipped == suit;
    }

    private static bool IsInjectorSlot(string? slotId)
    {
        return slotId is HardsuitInjectorComponent.SlotOneId or HardsuitInjectorComponent.SlotTwoId;
    }

    private static EntityUid? GetActionEntity(HardsuitInjectorComponent component, string slotId)
    {
        return slotId == HardsuitInjectorComponent.SlotOneId
            ? component.SlotOneActionEntity
            : component.SlotTwoActionEntity;
    }

    private static FixedPoint2 GetTransferAmount(HardsuitInjectorComponent component, string slotId)
    {
        return slotId == HardsuitInjectorComponent.SlotOneId
            ? component.SlotOneTransferAmount
            : component.SlotTwoTransferAmount;
    }

    private static void SetTransferAmount(HardsuitInjectorComponent component, string slotId, FixedPoint2 amount)
    {
        if (slotId == HardsuitInjectorComponent.SlotOneId)
            component.SlotOneTransferAmount = amount;
        else
            component.SlotTwoTransferAmount = amount;
    }
}
