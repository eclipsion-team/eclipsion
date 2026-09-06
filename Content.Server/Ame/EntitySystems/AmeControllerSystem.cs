using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Ame.Components;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.NodeContainer;
using Content.Server.Power.Components;
using Content.Shared.Ame.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared.Mind.Components;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Ame.EntitySystems;

public sealed class AmeControllerSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly AppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AmeControllerComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<AmeControllerComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<AmeControllerComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<AmeControllerComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<AmeControllerComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<AmeControllerComponent, UiButtonPressedMessage>(OnUiButtonPressed);
    }

    private void OnInit(EntityUid uid, AmeControllerComponent component, ComponentInit args)
    {
        _itemSlots.AddItemSlot(uid, SharedAmeControllerComponent.FuelSlotId, component.FuelSlot);

        UpdateUi(uid, component);
    }

    public override void Update(float frameTime)
    {
        var curTime = _gameTiming.CurTime;
        var query = EntityQueryEnumerator<AmeControllerComponent, NodeContainerComponent>();
        while (query.MoveNext(out var uid, out var controller, out var nodes))
        {
            // Give operators a chance to stop the active overload during the final countdown.
            if (controller.ExplosionTime != null)
            {
                if (!TryGetAMENodeGroup(uid, out var group, nodes) || !IsActivelyOverloading(controller, group))
                {
                    controller.ExplosionTime = null;
                }
                else
                {
                    if (curTime >= controller.ExplosionTime.Value)
                    {
                        controller.ExplosionTime = null;
                        group.ExplodeCores();
                    }
                    continue;
                }
            }

            if (controller.NextUpdate <= curTime)
                UpdateController(uid, curTime, controller, nodes);
            else if (controller.NextUIUpdate <= curTime)
                UpdateUi(uid, controller);
        }
    }

    private void OnRemove(EntityUid uid, AmeControllerComponent component, ComponentRemove args)
    {
        _itemSlots.RemoveItemSlot(uid, component.FuelSlot);
    }

    private void OnItemSlotChanged(EntityUid uid, AmeControllerComponent component, ContainerModifiedMessage args)
    {
        if (!component.Initialized)
            return;

        if (args.Container.ID != component.FuelSlot.ID)
            return;

        UpdateUi(uid, component);
    }

    private void UpdateController(EntityUid uid, TimeSpan curTime, AmeControllerComponent? controller = null, NodeContainerComponent? nodes = null)
    {
        if (!Resolve(uid, ref controller))
            return;

        controller.LastUpdate = curTime;
        controller.NextUpdate = curTime + controller.UpdatePeriod;
        // update the UI regardless of other factors to update the power readings
        UpdateUi(uid, controller);

        if (!TryGetAMENodeGroup(uid, out var group, nodes))
            return;

        var overloading = false;
        if (controller.Injecting && TryComp<AmeFuelContainerComponent>(controller.FuelSlot.Item, out var fuelContainer))
        {
            // if the jar is empty shut down the AME
            if (fuelContainer.FuelAmount <= 0)
            {
                SetInjecting(uid, false, null, controller);
            }
            else
            {
                var availableInject = Math.Min(controller.InjectionAmount, fuelContainer.FuelAmount);
                var powerOutput = group.InjectFuel(availableInject, out overloading);
                if (TryComp<PowerSupplierComponent>(uid, out var powerOutlet))
                    powerOutlet.MaxSupply = powerOutput;
                fuelContainer.FuelAmount -= availableInject;
                // only play audio if we actually had an injection
                if (availableInject > 0)
                    _audioSystem.PlayPvs(controller.InjectSound, uid, AudioParams.Default.WithVolume(overloading ? 10f : 0f));
                if (overloading)
                    AnnounceOverload(uid, curTime, controller);
                UpdateUi(uid, controller);
            }
        }

        // Whenever the reactor is not actively being overloaded - shut down, or pulled back under the
        // safe limit - the cores anneal. Without this, surviving an overload left the controller stuck
        // on its critical readout permanently, since core integrity only ever went down.
        //
        // The anneal belongs to the node group, not the controller: several controllers can be wired into one
        // AME, and running this per controller both doubled the repair rate and let a switched-off controller
        // heal the very cores another one was overloading. Only the master runs it, and only when no controller
        // on the group is pushing an unsafe injection - this controller's own `overloading` is not enough.
        if (group.MasterController == uid && !IsGroupOverloading(group))
            group.RepairCores(controller.CoreRepairAmount);

        controller.Stability = group.GetTotalStability();

        group.UpdateCoreVisuals();
        UpdateDisplay(uid, controller.Stability, controller);

        // Once the reactor becomes critically unstable, arm a short countdown and warn the sector,
        // rather than detonating instantly, so there is always a heads-up before the blast.
        if (controller.Stability <= 0 && overloading && controller.ExplosionTime == null)
            ArmExplosion(uid, curTime, controller);
    }

    /// <summary>
    /// Whether the controller can still perform an unsafe injection with its current settings and fuel.
    /// </summary>
    private bool IsActivelyOverloading(AmeControllerComponent controller, AmeNodeGroup group)
    {
        if (!controller.Injecting ||
            !TryComp<AmeFuelContainerComponent>(controller.FuelSlot.Item, out var fuelContainer))
        {
            return false;
        }

        var availableInject = Math.Min(controller.InjectionAmount, fuelContainer.FuelAmount);
        return group.IsOverloading(availableInject);
    }

    /// <summary>
    /// Whether <em>any</em> controller wired into this group is currently pushing an unsafe injection, so that a
    /// second controller left switched off cannot anneal away the damage the first one is doing.
    /// </summary>
    private bool IsGroupOverloading(AmeNodeGroup group)
    {
        foreach (var node in group.Nodes)
        {
            if (TryComp<AmeControllerComponent>(node.Owner, out var other) && IsActivelyOverloading(other, group))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Schedules the explosion <see cref="AmeControllerComponent.FinalWarningTime"/> from now and broadcasts
    /// the final "imminent detonation" warning once. Stopping the overload cancels the countdown.
    /// </summary>
    private void ArmExplosion(EntityUid uid, TimeSpan curTime, AmeControllerComponent controller)
    {
        controller.ExplosionTime = curTime + controller.FinalWarningTime;

        var gridName = GetGridName(uid);
        var seconds = (int) controller.FinalWarningTime.TotalSeconds;
        var message = Loc.GetString("ame-imminent-announcement", ("grid", gridName), ("seconds", seconds));
        var sender = Loc.GetString("ame-overload-announcement-sender");
        _chatSystem.DispatchGlobalAnnouncement(message, sender, playSound: true, colorOverride: Color.Red);
    }

    /// <summary>
    /// Broadcasts a sector-wide warning that the AME on this controller's grid is overloading,
    /// naming the grid the engine is aboard. Rate-limited per controller to avoid spam.
    /// </summary>
    private void AnnounceOverload(EntityUid uid, TimeSpan curTime, AmeControllerComponent controller)
    {
        // Only warn a limited number of times per overload event; the imminent-detonation warning is separate.
        if (controller.OverloadAnnouncementsSent >= controller.MaxOverloadAnnouncements)
            return;

        if (curTime < controller.NextOverloadAnnouncement)
            return;

        controller.NextOverloadAnnouncement = curTime + controller.OverloadAnnouncementCooldown;
        controller.OverloadAnnouncementsSent++;

        var message = Loc.GetString("ame-overload-announcement", ("grid", GetGridName(uid)));
        var sender = Loc.GetString("ame-overload-announcement-sender");
        _chatSystem.DispatchGlobalAnnouncement(message, sender, playSound: true, colorOverride: Color.Red);
    }

    /// <summary>
    /// Name the grid (ship/station) the engine is aboard so responders know where to go.
    /// </summary>
    private string GetGridName(EntityUid uid)
    {
        var gridUid = Transform(uid).GridUid;
        return gridUid != null ? Name(gridUid.Value) : Loc.GetString("ame-overload-announcement-unknown-grid");
    }

    public void UpdateUi(EntityUid uid, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return;

        if (!_userInterfaceSystem.HasUi(uid, AmeControllerUiKey.Key))
            return;

        var state = GetUiState(uid, controller);
        _userInterfaceSystem.SetUiState(uid, AmeControllerUiKey.Key, state);

        controller.NextUIUpdate = _gameTiming.CurTime + controller.UpdateUIPeriod;
    }

    private AmeControllerBoundUserInterfaceState GetUiState(EntityUid uid, AmeControllerComponent controller)
    {
        var powered = !TryComp<ApcPowerReceiverComponent>(uid, out var powerSource) || powerSource.Powered;
        var coreCount = 0;
        // how much power can be produced at the current settings, in kW
        // we don't use max. here since this is what is set in the Controller, not what the AME is actually producing
        float targetedPowerSupply = 0;
        if (TryGetAMENodeGroup(uid, out var group))
        {
            coreCount = group.CoreCount;
            targetedPowerSupply = group.CalculatePower(controller.InjectionAmount, group.CoreCount) / 1000;
        }

        // set current power statistics in kW
        float currentPowerSupply = 0;
        if (TryComp<PowerSupplierComponent>(uid, out var powerOutlet) && coreCount > 0)
        {
            currentPowerSupply = powerOutlet.CurrentSupply / 1000;
        }

        var fuelContainerInSlot = controller.FuelSlot.Item;
        var hasFuelContainerInSlot = Exists(fuelContainerInSlot);
        if (!hasFuelContainerInSlot || !TryComp<AmeFuelContainerComponent>(fuelContainerInSlot, out var fuelContainer))
            return new AmeControllerBoundUserInterfaceState(powered,
                                                            IsMasterController(uid),
                                                            false,
                                                            hasFuelContainerInSlot,
                                                            0,
                                                            controller.InjectionAmount,
                                                            coreCount,
                                                            currentPowerSupply,
                                                            targetedPowerSupply);

        return new AmeControllerBoundUserInterfaceState(powered,
                                                        IsMasterController(uid),
                                                        controller.Injecting,
                                                        hasFuelContainerInSlot,
                                                        fuelContainer.FuelAmount,
                                                        controller.InjectionAmount,
                                                        coreCount,
                                                        currentPowerSupply,
                                                        targetedPowerSupply);
    }

    private bool IsMasterController(EntityUid uid)
    {
        return TryGetAMENodeGroup(uid, out var group) && group.MasterController == uid;
    }

    private bool TryGetAMENodeGroup(EntityUid uid, [MaybeNullWhen(false)] out AmeNodeGroup group, NodeContainerComponent? nodes = null)
    {
        if (!Resolve(uid, ref nodes))
        {
            group = null;
            return false;
        }

        group = nodes.Nodes.Values
            .Select(node => node.NodeGroup)
            .OfType<AmeNodeGroup>()
            .FirstOrDefault();

        return group != null;
    }

    public void TryEject(EntityUid uid, EntityUid? user = null, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return;

        if (controller.Injecting)
            return;

        if (!Exists(controller.FuelSlot.Item))
            return;

        _itemSlots.TryEjectToHands(uid, controller.FuelSlot, user);

        UpdateUi(uid, controller);
    }

    public void SetInjecting(EntityUid uid, bool value, EntityUid? user = null, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return;

        if (controller.Injecting == value)
            return;

        controller.Injecting = value;
        UpdateDisplay(uid, controller.Stability, controller);
        if (!value)
        {
            // Shutting the reactor down during the final warning makes it safe immediately.
            controller.ExplosionTime = null;

            // Overload event is over; let a future overload warn again.
            controller.OverloadAnnouncementsSent = 0;
            controller.NextOverloadAnnouncement = TimeSpan.Zero;

            if (TryComp<PowerSupplierComponent>(uid, out var powerOut))
                powerOut.MaxSupply = 0;
        }

        UpdateUi(uid, controller);

        _itemSlots.SetLock(uid, controller.FuelSlot, value);

        // Logging
        if (!HasComp<MindContainerComponent>(user))
            return;

        var humanReadableState = value ? "Inject" : "Not inject";
        _adminLogger.Add(LogType.Action, LogImpact.Extreme, $"{EntityManager.ToPrettyString(user.Value):player} has set the AME to {humanReadableState}");
    }

    public void ToggleInjecting(EntityUid uid, EntityUid? user = null, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return;
        SetInjecting(uid, !controller.Injecting, user, controller);
    }

    public void SetInjectionAmount(EntityUid uid, int value, EntityUid? user = null, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return;
        if (controller.InjectionAmount == value)
            return;

        var oldValue = controller.InjectionAmount;
        controller.InjectionAmount = value;

        UpdateUi(uid, controller);

        // Logging
        if (!TryComp<MindContainerComponent>(user, out var mindContainer))
            return;

        var humanReadableState = controller.Injecting ? "Inject" : "Not inject";
        _adminLogger.Add(LogType.Action, LogImpact.Extreme, $"{EntityManager.ToPrettyString(user.Value):player} has set the AME to inject {controller.InjectionAmount} while set to {humanReadableState}");

        // Admin alert
        var safeLimit = 0;
        if (TryGetAMENodeGroup(uid, out var group))
            safeLimit = group.GetSafeFuelLimit();

        if (oldValue <= safeLimit && value > safeLimit)
        {
            if (_gameTiming.CurTime > controller.EffectCooldown)
            {
                _chatManager.SendAdminAlert(user.Value, $"increased AME over safe limit to {controller.InjectionAmount}");
                _audioSystem.PlayGlobal("/Audio/Misc/adminlarm.ogg",
                    Filter.Empty().AddPlayers(_adminManager.ActiveAdmins), false, AudioParams.Default.WithVolume(-8f));
                controller.EffectCooldown = _gameTiming.CurTime + controller.CooldownDuration;
            }
        }
    }

    public void AdjustInjectionAmount(EntityUid uid, int delta, int min = 0, int max = int.MaxValue, EntityUid? user = null, AmeControllerComponent? controller = null)
    {
        if (Resolve(uid, ref controller))
            SetInjectionAmount(uid, MathHelper.Clamp(controller.InjectionAmount + delta, min, max), user, controller);
    }

    private void UpdateDisplay(EntityUid uid, int stability, AmeControllerComponent? controller = null, AppearanceComponent? appearance = null)
    {
        if (!Resolve(uid, ref controller, ref appearance))
            return;

        var ameControllerState = stability switch
        {
            < 10 => AmeControllerState.Fuck,
            < 50 => AmeControllerState.Critical,
            < 80 => AmeControllerState.Warning,
            _ => AmeControllerState.On,
        };

        if (!controller.Injecting)
            ameControllerState = AmeControllerState.Off;

        _appearanceSystem.SetData(
            uid,
            AmeControllerVisuals.DisplayState,
            ameControllerState,
            appearance
        );
    }

    private void OnPowerChanged(EntityUid uid, AmeControllerComponent comp, ref PowerChangedEvent args)
    {
        UpdateUi(uid, comp);
    }

    private void OnUiButtonPressed(EntityUid uid, AmeControllerComponent comp, UiButtonPressedMessage msg)
    {
        var user = msg.Actor;
        if (!Exists(user))
            return;

        var needsPower = msg.Button switch
        {
            UiButton.Eject => false,
            _ => true,
        };

        if (!PlayerCanUseController(uid, user, needsPower, comp))
            return;

        _audioSystem.PlayPvs(comp.ClickSound, uid, AudioParams.Default.WithVolume(-2f));
        switch (msg.Button)
        {
            case UiButton.Eject:
                TryEject(uid, user: user, controller: comp);
                break;
            case UiButton.ToggleInjection:
                ToggleInjecting(uid, user: user, controller: comp);
                break;
            case UiButton.IncreaseFuel:
                AdjustInjectionAmount(uid, +2, user: user, controller: comp);
                break;
            case UiButton.DecreaseFuel:
                AdjustInjectionAmount(uid, -2, user: user, controller: comp);
                break;
        }

        if (TryGetAMENodeGroup(uid, out var group))
            group.UpdateCoreVisuals();

        UpdateUi(uid, comp);
    }

    /// <summary>
    /// Checks whether the player entity is able to use the controller.
    /// </summary>
    /// <param name="playerEntity">The player entity.</param>
    /// <returns>Returns true if the entity can use the controller, and false if it cannot.</returns>
    private bool PlayerCanUseController(EntityUid uid, EntityUid playerEntity, bool needsPower = true, AmeControllerComponent? controller = null)
    {
        if (!Resolve(uid, ref controller))
            return false;

        //Need player entity to check if they are still able to use the dispenser
        if (!Exists(playerEntity))
            return false;

        //Check if device is powered
        if (needsPower && TryComp<ApcPowerReceiverComponent>(uid, out var powerSource) && !powerSource.Powered)
            return false;

        return true;
    }
}
