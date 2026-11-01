using System.Text.Json;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.GameTicking;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Gives every independent who claims ground their own owner id (IND1, IND2...), kept per player across rounds and
/// campaigns, so no independent can use another's territory.
/// </summary>
public sealed class TerritoryIndependentSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly NpcFactionSystem _npcFactions = default!;

    private static readonly ResPath SavePath = new("/territories/independents.json");

    /// <summary>NPC faction of an independent's turrets: hostile to every IND, the holder is let through by body.</summary>
    public const string TurretFaction = "IndependentTerritory";

    private readonly Dictionary<NetUserId, string> _owners = new();
    private int _highest;

    public override void Initialize()
    {
        base.Initialize();

        Load();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete, before: [typeof(TerritorySpawnBeaconSystem)]);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (_owners.TryGetValue(ev.Player.UserId, out var owner))
            Apply(ev.Mob, owner);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (_owners.TryGetValue(ev.Player.UserId, out var owner))
            Apply(ev.Entity, owner);
    }

    /// <summary>
    /// The independent's owner id, numbering them on their first claim. Null for anyone outside the IND faction.
    /// </summary>
    public string? EnsureOwner(EntityUid body)
    {
        if (!TryComp<ActorComponent>(body, out var actor))
            return null;

        var user = actor.PlayerSession.UserId;
        if (!_owners.TryGetValue(user, out var owner))
        {
            if (CompOrNull<HullrotFactionComponent>(body)?.Faction != PersistentTerritoryFactions.IndependentFaction)
                return null;

            owner = $"{PersistentTerritoryFactions.IndependentFaction}{++_highest}";
            _owners[user] = owner;
            Save();
        }

        Apply(body, owner);
        return PersistentTerritoryFactions.GetTeam(EntityManager, body) == owner ? owner : null;
    }

    public string? GetOwner(NetUserId user)
    {
        return _owners.GetValueOrDefault(user);
    }

    private void Apply(EntityUid body, string owner)
    {
        var comp = EnsureComp<TerritoryIndependentComponent>(body);
        if (comp.Owner == owner)
            return;

        comp.Owner = owner;
        Dirty(body, comp);

        if (PersistentTerritoryFactions.GetTeam(EntityManager, body) != owner)
            return;

        var query = EntityQueryEnumerator<CaptureRegionDeviceComponent, NpcFactionMemberComponent, TransformComponent>();
        while (query.MoveNext(out var device, out var link, out _, out var xform))
        {
            if (link.UpdateNpcFaction &&
                xform.GridUid is { } grid &&
                TryComp<PersistentPoiGridComponent>(grid, out var territory) &&
                territory.Owner == owner)
            {
                _npcFactions.IgnoreEntity(device, body);
            }
        }
    }

    /// <summary>Lets the holder's bodies past a device that just came under their control.</summary>
    public void ExemptHolder(EntityUid device, string owner)
    {
        var query = EntityQueryEnumerator<TerritoryIndependentComponent>();
        while (query.MoveNext(out var body, out var independent))
        {
            if (independent.Owner == owner && PersistentTerritoryFactions.GetTeam(EntityManager, body) == owner)
                _npcFactions.IgnoreEntity(device, body);
        }
    }

    private void Load()
    {
        _owners.Clear();
        _highest = 0;

        try
        {
            if (!_resources.UserData.Exists(SavePath) || !_resources.UserData.TryReadAllText(SavePath, out var json))
                return;

            var data = JsonSerializer.Deserialize<Dictionary<Guid, string>>(json);
            foreach (var (user, owner) in data ?? new())
            {
                if (!PersistentTerritoryFactions.IsIndependent(owner))
                    continue;

                _owners[new NetUserId(user)] = owner;
                if (int.TryParse(owner.AsSpan(PersistentTerritoryFactions.IndependentFaction.Length), out var number))
                    _highest = Math.Max(_highest, number);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load independent territory owners: {e}");
        }
    }

    private void Save()
    {
        try
        {
            var data = new Dictionary<Guid, string>();
            foreach (var (user, owner) in _owners)
                data[user.UserId] = owner;

            _resources.UserData.CreateDir(SavePath.Directory);
            _resources.UserData.WriteAllText(SavePath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save independent territory owners: {e}");
        }
    }
}
