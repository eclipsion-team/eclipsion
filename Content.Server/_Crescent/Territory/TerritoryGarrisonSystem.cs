using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.GameTicking;
using Content.Shared._Crescent.Territory;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Crescent.Territory;

public sealed class TerritoryGarrisonSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PersistentPoiLevelSystem _levels = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TerritoryDropPodSystem _pods = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    // pods of one call come down a little apart
    private const float PodSpacing = 0.6f;

    // tiles around a console that called soldiers in
    private const int NearRadius = 3;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
    }

    // round start fills the garrison for free; after that the recruitment console refills it
    private void OnRoundStarted(RoundStartedEvent ev)
    {
        var query = EntityQueryEnumerator<TerritoryGarrisonComponent, PersistentCaptureRegionComponent>();
        while (query.MoveNext(out var uid, out var garrison, out var region))
        {
            if (region.ValidRegion)
                SpawnGarrison((uid, garrison));
        }
    }

    public int SpawnGarrison(Entity<TerritoryGarrisonComponent> standard)
    {
        if (Transform(standard).GridUid is not { } grid ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner == null)
        {
            return 0;
        }

        return CallIn(standard, TerritoryRecruitKind.Garrison, _levels.GetLevel(territory.Level)?.GarrisonSoldiers ?? 0);
    }

    /// <summary>
    /// Drops <paramref name="count"/> of the holder's soldiers in by pod, around <paramref name="near"/> if given,
    /// otherwise around the standard. With no <paramref name="role"/> each pod brings a random one.
    /// </summary>
    public int CallIn(Entity<TerritoryGarrisonComponent> standard, TerritoryRecruitKind kind, int count,
        ProtoId<TerritorySoldierRolePrototype>? role = null, EntityCoordinates? near = null, EntityUid? recruiter = null)
    {
        var xform = Transform(standard);
        if (count <= 0 ||
            xform.GridUid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var gridComp) ||
            !TryComp<PersistentPoiGridComponent>(grid, out var territory) ||
            territory.Owner is not { } owner ||
            !Offers(owner, kind))
        {
            return 0;
        }

        var roster = new List<EntProtoId>();
        foreach (var offered in GetRoles(standard, owner))
        {
            if (role == null || offered.ID == role.Value.Id)
                roster.Add(offered.Soldiers[RosterKey(owner)]);
        }

        if (roster.Count == 0)
            return 0;

        var posts = near is { } spot
            ? FindPosts(grid, gridComp, _map.LocalToTile(grid, gridComp, spot), NearRadius)
            : new List<Vector2i>();

        if (posts.Count == 0)
            posts = FindPosts(grid, gridComp, _map.LocalToTile(grid, gridComp, xform.Coordinates), standard.Comp.SpawnRadius);

        if (posts.Count == 0)
        {
            Log.Warning($"No free floor around {ToPrettyString(standard)} to drop soldiers on.");
            return 0;
        }

        _random.Shuffle(posts);
        for (var i = 0; i < count; i++)
        {
            _pods.Launch(_map.GridTileToLocal(grid, gridComp, posts[i % posts.Count]),
                _random.Pick(roster),
                owner,
                territory.RegionId,
                kind == TerritoryRecruitKind.Escort,
                recruiter,
                i * PodSpacing);
        }

        return count;
    }

    /// <summary>
    /// The faction's soldiers of this kind from this territory still standing, and those still coming down.
    /// </summary>
    public int CountSoldiers(string regionId, string faction, TerritoryRecruitKind kind)
    {
        var escort = kind == TerritoryRecruitKind.Escort;
        var count = 0;

        var soldiers = EntityQueryEnumerator<TerritoryGarrisonSoldierComponent>();
        while (soldiers.MoveNext(out var uid, out var soldier))
        {
            if (soldier.Escort == escort &&
                Matches(soldier.RegionId, soldier.Faction, regionId, faction) &&
                !_mobState.IsDead(uid))
            {
                count++;
            }
        }

        var pods = EntityQueryEnumerator<TerritoryDropPodPayloadComponent>();
        while (pods.MoveNext(out _, out var payload))
        {
            if (payload.Escort == escort && Matches(payload.RegionId, payload.Faction, regionId, faction))
                count++;
        }

        return count;
    }

    // escorts still in the air on their way to this player
    public int CountInbound(EntityUid recruiter)
    {
        var count = 0;
        var pods = EntityQueryEnumerator<TerritoryDropPodPayloadComponent>();
        while (pods.MoveNext(out _, out var payload))
        {
            if (payload.Recruiter == recruiter)
                count++;
        }

        return count;
    }

    public bool HasSoldiers(Entity<TerritoryGarrisonComponent> standard, string faction)
    {
        return GetRoles(standard, faction).Any();
    }

    // the roles the standard offers that the faction has soldiers for, in menu order
    public IEnumerable<TerritorySoldierRolePrototype> GetRoles(Entity<TerritoryGarrisonComponent> standard, string faction)
    {
        var key = RosterKey(faction);
        foreach (var id in standard.Comp.Roles)
        {
            if (_proto.TryIndex(id, out var role) && role.Soldiers.ContainsKey(key))
                yield return role;
        }
    }

    /// <summary>Independents only hire mercenaries to follow them; nobody holds their ground for them.</summary>
    public static bool Offers(string owner, TerritoryRecruitKind kind)
    {
        return kind == TerritoryRecruitKind.Escort || !PersistentTerritoryFactions.IsIndependent(owner);
    }

    // every independent hires from the one IND roster
    private static string RosterKey(string owner)
    {
        return PersistentTerritoryFactions.IsIndependent(owner) ? PersistentTerritoryFactions.IndependentFaction : owner;
    }

    public bool TryGetStandard(EntityUid grid, [NotNullWhen(true)] out Entity<TerritoryGarrisonComponent>? standard)
    {
        var query = EntityQueryEnumerator<TerritoryGarrisonComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var garrison, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            standard = (uid, garrison);
            return true;
        }

        standard = null;
        return false;
    }

    private static bool Matches(string regionId, string faction, string wantedRegion, string wantedFaction)
    {
        return string.Equals(regionId, wantedRegion, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(faction, wantedFaction, StringComparison.Ordinal);
    }

    private List<Vector2i> FindPosts(EntityUid grid, MapGridComponent gridComp, Vector2i center, int radius)
    {
        var posts = new List<Vector2i>();
        for (var x = -radius; x <= radius; x++)
        {
            for (var y = -radius; y <= radius; y++)
            {
                if (x * x + y * y > radius * radius)
                    continue;

                var indices = center + new Vector2i(x, y);
                if (!_map.TryGetTileRef(grid, gridComp, indices, out var tile) ||
                    tile.Tile.IsEmpty ||
                    _turf.IsTileBlocked(grid, indices, CollisionGroup.MobMask, gridComp))
                {
                    continue;
                }

                posts.Add(indices);
            }
        }

        return posts;
    }
}
