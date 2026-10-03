using Content.Shared.Body.Systems;
using Content.Shared.Mobs;
using Content.Shared._Shitmed.Targeting;
using Content.Shared._Shitmed.Targeting.Events;
using Content.Shared.Body.Part;

namespace Content.Server._Shitmed.Targeting;
public sealed class TargetingSystem : SharedTargetingSystem
{
    [Dependency] private readonly SharedBodySystem _bodySystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<TargetChangeEvent>(OnTargetChange);
        SubscribeLocalEvent<TargetingComponent, MobStateChangedEvent>(OnMobStateChange);
    }

    private void OnTargetChange(TargetChangeEvent message, EntitySessionEventArgs args)
    {
        // A client may only change its own aim, and only to a single body part; anything else is a forged message.
        var uid = GetEntity(message.Uid);
        if (args.SenderSession.AttachedEntity != uid || !IsSinglePart(message.BodyPart))
            return;

        if (!TryComp<TargetingComponent>(uid, out var target))
            return;

        target.Target = message.BodyPart;
        Dirty(uid, target);
    }

    private static bool IsSinglePart(TargetBodyPart part)
    {
        var value = (ushort) part;
        return value != 0 && (value & (value - 1)) == 0 && part <= TargetBodyPart.RightFoot;
    }

    private void OnMobStateChange(EntityUid uid, TargetingComponent component, MobStateChangedEvent args)
    {
        // Revival is handled by the server, so we're keeping all of this here.
        var changed = false;

        if (args.NewMobState == MobState.Dead)
        {
            foreach (var part in GetValidParts())
            {
                component.BodyStatus[part] = TargetIntegrity.Dead;
                changed = true;
            }
            // I love groin shitcode.
            component.BodyStatus[TargetBodyPart.Groin] = TargetIntegrity.Dead;
        }
        else if (args.OldMobState == MobState.Dead && (args.NewMobState == MobState.Alive || args.NewMobState == MobState.Critical))
        {
            component.BodyStatus = _bodySystem.GetBodyPartStatus(uid);
            changed = true;
        }

        if (changed)
        {
            Dirty(uid, component);
            RaiseNetworkEvent(new TargetIntegrityChangeEvent(GetNetEntity(uid)), uid);
        }
    }
}
