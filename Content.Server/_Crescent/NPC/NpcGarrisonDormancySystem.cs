using Content.Shared._Shitmed.Body.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.NPC;

public sealed class NpcGarrisonDormancySystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly HungerSystem _hunger = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ThirstSystem _thirst = default!;

    private readonly List<MapCoordinates> _players = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcGarrisonDormancyComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<NpcGarrisonDormancyComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Dormant && !TerminatingOrDeleted(ent))
            RemComp<BreathingImmunityComponent>(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var gathered = false;

        var query = EntityQueryEnumerator<NpcGarrisonDormancyComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var garrison, out var xform))
        {
            if (now < garrison.NextCheck)
                continue;

            garrison.NextCheck = now + garrison.CheckInterval;

            if (!gathered)
            {
                GatherPlayers();
                gathered = true;
            }

            if (_mobState.IsDead(uid) || HasComp<ActorComponent>(uid) || IsWatched(_transform.GetMapCoordinates(uid, xform), garrison.Range))
                Wake((uid, garrison));
            else
                Sleep((uid, garrison));
        }
    }

    private void GatherPlayers()
    {
        _players.Clear();

        // bodies only: a ghost drifting past is nobody to fight
        var actors = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (actors.MoveNext(out var uid, out _, out _, out var xform))
        {
            _players.Add(_transform.GetMapCoordinates(uid, xform));
        }
    }

    private bool IsWatched(MapCoordinates position, float range)
    {
        var rangeSquared = range * range;
        foreach (var player in _players)
        {
            if (player.MapId == position.MapId && (player.Position - position.Position).LengthSquared() <= rangeSquared)
                return true;
        }

        return false;
    }

    private void Sleep(Entity<NpcGarrisonDormancyComponent> ent)
    {
        if (!ent.Comp.Dormant && !HasComp<BreathingImmunityComponent>(ent))
        {
            AddComp<BreathingImmunityComponent>(ent);
            ent.Comp.Dormant = true;
        }

        if (TryComp<HungerComponent>(ent, out var hunger))
            _hunger.SetHunger(ent, hunger.Thresholds[HungerThreshold.Okay], hunger);

        if (TryComp<ThirstComponent>(ent, out var thirst))
            _thirst.SetThirst(ent, thirst, thirst.ThirstThresholds[ThirstThreshold.Okay]);
    }

    private void Wake(Entity<NpcGarrisonDormancyComponent> ent)
    {
        if (!ent.Comp.Dormant)
            return;

        RemComp<BreathingImmunityComponent>(ent);
        ent.Comp.Dormant = false;
    }
}
