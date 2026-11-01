using Content.Shared._Crescent.Territory;
using Content.Shared.CaptureFlag;
using Robust.Client.GameObjects;

namespace Content.Client._Crescent.Territory;

/// <summary>
/// Owns visuals only for the special persistent freeplay territory flag. Ordinary CaptureFlag and station
/// ConquestFlag visuals remain in their existing systems.
/// </summary>
public sealed class PersistentCaptureRegionVisualsSystem : EntitySystem
{
    private readonly Dictionary<EntityUid, string?> _displayedOwners = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentCaptureRegionComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<PersistentCaptureRegionComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<PersistentCaptureRegionComponent> ent, ref ComponentStartup args)
    {
        UpdateVisuals(ent);
    }

    private void OnShutdown(Entity<PersistentCaptureRegionComponent> ent, ref ComponentShutdown args)
    {
        _displayedOwners.Remove(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<PersistentCaptureRegionComponent, CaptureFlagComponent>();
        while (query.MoveNext(out var uid, out var region, out var flag))
        {
            if (_displayedOwners.TryGetValue(uid, out var displayedOwner) &&
                displayedOwner == flag.OwnerTeam)
            {
                continue;
            }

            UpdateVisuals((uid, region), flag);
        }
    }

    private void UpdateVisuals(Entity<PersistentCaptureRegionComponent> ent, CaptureFlagComponent? flag = null)
    {
        if (!Resolve(ent, ref flag, false) ||
            !TryComp<SpriteComponent>(ent, out var sprite))
        {
            return;
        }

        var state = flag.NeutralState;
        var team = PersistentTerritoryFactions.IsIndependent(flag.OwnerTeam)
            ? PersistentTerritoryFactions.IndependentFaction
            : flag.OwnerTeam;

        if (team != null && ent.Comp.TeamStates.TryGetValue(team, out var configuredState))
            state = configuredState;

        sprite.LayerSetState(0, state);
        _displayedOwners[ent] = flag.OwnerTeam;
    }
}
