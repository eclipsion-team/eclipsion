using Content.Shared._Crescent.Aggression;
using Content.Shared._Crescent.Audio;
using Content.Shared.Mobs;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Player;

namespace Content.Server._Crescent.Mobs;

public sealed class MegafaunaSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MegafaunaComponent, AttackedEvent>(OnAttacked);
        SubscribeLocalEvent<MegafaunaComponent, MobStateChangedEvent>(OnDeath);
    }

    public void OnAttacked<T>(EntityUid uid, T comp, ref AttackedEvent args) where T : MegafaunaComponent
    {
        if (!HasComp<MegafaunaWeaponLooterComponent>(args.Used))
            comp.CrusherOnly = false; // it's over...
    }

    public void OnDeath<T>(EntityUid uid, T comp, ref MobStateChangedEvent args) where T : MegafaunaComponent
    {
        // Raised for every state transition, not just death.
        if (args.NewMobState != MobState.Dead)
            return;

        var coords = Transform(uid).Coordinates;

        comp.CancelToken.Cancel();

        RaiseLocalEvent(uid, new MegafaunaKilledEvent());

        if (TryComp<BossMusicComponent>(uid, out var boss) &&
            TryComp<AggressiveComponent>(uid, out var aggresive))
        {
            var msg = new BossMusicStopEvent();
            foreach (var aggressor in aggresive.Aggressors)
            {
                // One aggressor without a player attached must not stop the music for everyone else.
                if (!TryComp<ActorComponent>(aggressor, out var actor))
                    continue;

                RaiseNetworkEvent(msg, actor.PlayerSession.Channel);
            }
        }

        if (comp.CrusherOnly && comp.CrusherLoot != null)
        {
            Spawn(comp.CrusherLoot, coords);
        }
        else if (comp.Loot != null)
        {
            Spawn(comp.Loot, coords);
        }

        QueueDel(uid);
    }
}
