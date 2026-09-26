using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared.Clothing.Loadouts.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

/// <inheritdoc cref="NpcStartInternalsComponent"/>
public sealed class NpcStartInternalsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly GasTankSystem _gasTank = default!;
    [Dependency] private readonly InternalsSystem _internals = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcTacticalSystem _tactical = default!;
    [Dependency] private readonly RespiratorSystem _respirator = default!;

    public override void Initialize()
    {
        base.Initialize();

        // The loadout is what puts the mask and the tank on them, so this has to come after it.
        SubscribeLocalEvent<NpcStartInternalsComponent, MapInitEvent>(OnMapInit,
            after: [typeof(SharedLoadoutSystem)]);
    }

    private void OnMapInit(Entity<NpcStartInternalsComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextCheck = _timing.CurTime + ent.Comp.CheckInterval;

        if (!TryComp<InternalsComponent>(ent, out var internals))
            return;

        // ToggleInternals is a toggle, not a switch-on: if the tank is already hooked up it disconnects
        // it again. InternalsSystem connects it itself when the mob is equipped somewhere it can't
        // breathe, which is exactly the case this component exists for, so check before toggling.
        // Somewhere it can breathe, the first check closes it again after SwitchOffDelay.
        if (_internals.AreInternalsWorking(ent, internals))
            return;

        _internals.ToggleInternals(ent, ent, true, internals);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent>();
        while (query.MoveNext(out var uid, out var comp, out var internals, out var respirator))
        {
            if (now < comp.NextCheck)
                continue;

            comp.NextCheck = now + comp.CheckInterval;

            // The dead don't breathe. The critical still do, and a wounded soldier in vacuum needs its tank most.
            if (_mobState.IsDead(uid) || internals.BreathTools.Count == 0)
                continue;

            UpdateInternals((uid, comp, internals, respirator), now);
        }
    }

    private void UpdateInternals(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        TimeSpan now)
    {
        var (uid, comp, internals, respirator) = ent;

        var air = _atmos.GetContainingMixture(uid);
        if (air != null && _respirator.CanMetabolizeGas((uid, respirator), air))
        {
            comp.BreathableSince ??= now;

            if (_internals.AreInternalsWorking(internals) && now - comp.BreathableSince >= comp.SwitchOffDelay)
                _internals.DisconnectTank(internals);

            return;
        }

        comp.BreathableSince = null;

        // Already on a tank with air left in it: nothing to do - unless that tank is its jetpack, which
        // internals hooks up first, and there is something else to breathe.
        if (_internals.AreInternalsWorking(internals)
            && TryComp<GasTankComponent>(internals.GasTankEntity, out var current)
            && !current.IsLowPressure
            && _respirator.CanMetabolizeGas((uid, respirator), current.Air)
            && (!HasComp<JetpackComponent>(internals.GasTankEntity) || FindFullestTank(ent, jetpacks: false) == null))
        {
            return;
        }

        if (FindFullestTank(ent) is not { } best || best.Owner == internals.GasTankEntity)
            return;

        _gasTank.ConnectToInternals(best);
    }

    /// <summary>
    /// The carried tank holding the most gas the NPC can actually breathe - so an emptied tank is swapped for
    /// a full one, and a plasma tank picked up somewhere is never put on. A jetpack is only breathed from once
    /// nothing else is left: its gas is the NPC's way back if it ends up in space (NpcJetpack).
    /// </summary>
    private Entity<GasTankComponent>? FindFullestTank(Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent)
    {
        return FindFullestTank(ent, jetpacks: false) ?? FindFullestTank(ent, jetpacks: true);
    }

    private Entity<GasTankComponent>? FindFullestTank(
        Entity<NpcStartInternalsComponent, InternalsComponent, RespiratorComponent> ent,
        bool jetpacks)
    {
        Entity<GasTankComponent>? best = null;
        var bestMoles = 0f;

        foreach (var item in _tactical.EnumerateCarried(ent))
        {
            if (!TryComp<GasTankComponent>(item, out var tank) || !tank.IsInternals || tank.IsLowPressure)
                continue;

            if (HasComp<JetpackComponent>(item) != jetpacks)
                continue;

            // Open valves can't be connected; the one it is already on is fine.
            if (item != ent.Comp2.GasTankEntity && !_gasTank.CanConnectToInternals(tank))
                continue;

            var moles = tank.Air.TotalMoles;
            if (moles <= bestMoles || !_respirator.CanMetabolizeGas((ent.Owner, ent.Comp3), tank.Air))
                continue;

            best = (item, tank);
            bestMoles = moles;
        }

        return best;
    }
}
