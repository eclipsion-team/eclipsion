using Content.Shared._Crescent.Mind;
using Content.Shared._Shitmed.Body.Events;
using Content.Shared.Body.Components;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;

namespace Content.Server._Crescent.Mind;

/// <summary>
/// Marks bodies and detached limbs that belonged to a player, even after their mind leaves.
/// </summary>
public sealed class HadMindSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MindContainerComponent, MindAddedMessage>(OnMindAdded);
        SubscribeLocalEvent<BodyComponent, BodyPartDroppedEvent>(OnBodyPartDropped);
    }

    private void OnMindAdded(Entity<MindContainerComponent> ent, ref MindAddedMessage args)
    {
        EnsureComp<HadMindComponent>(ent);
    }

    /// <summary>
    ///     When a body part is detached from a body that had a mind,
    ///     copy the HadMindComponent to the detached part.
    /// </summary>
    private void OnBodyPartDropped(EntityUid uid, BodyComponent comp, ref BodyPartDroppedEvent args)
    {
        // Deleting a body also drops its parts; they cannot receive components during teardown.
        if (!TerminatingOrDeleted(args.Part) && HasComp<HadMindComponent>(uid))
        {
            EnsureComp<HadMindComponent>(args.Part);
        }
    }
}
