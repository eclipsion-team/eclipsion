using System.Text.Json;
using Content.Server.Administration.Logs;
using Content.Server.Popups;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Territory;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Territory;

/// <summary>
/// Gives every independent who claims ground their own owner id (IND1, IND2...), kept per player across rounds and
/// campaigns, so no independent can use another's territory unless they were added as a member.
/// </summary>
public sealed class TerritoryIndependentSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly NpcFactionSystem _npcFactions = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly PopupSystem _popup = default!;

    private static readonly ResPath SavePath = new("/territories/independents.json");
    private static readonly ResPath MembersPath = new("/territories/independent_members.json");

    public const int MaxMembers = 6;

    /// <summary>NPC faction of an independent's turrets: hostile to every IND, the holder is let through by body.</summary>
    public const string TurretFaction = "IndependentTerritory";

    private readonly Dictionary<NetUserId, string> _owners = new();
    // people let into someone else's ground, they use that owner id instead of their own
    private readonly Dictionary<NetUserId, IndependentMember> _members = new();
    private int _highest;

    public override void Initialize()
    {
        base.Initialize();

        Load();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete, before: [typeof(TerritorySpawnBeaconSystem)]);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<TerritoryIndependentComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
    }

    // right click yourself to leave, the console on that ground used to be the only way out
    private void OnGetVerbs(Entity<TerritoryIndependentComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (args.User != args.Target || !TryComp<ActorComponent>(args.User, out var actor))
            return;

        var user = actor.PlayerSession.UserId;
        if (!_members.TryGetValue(user, out var member))
            return;

        var body = args.User;
        var owner = member.Owner;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("persistent-poi-admin-members-leave-verb", ("owner", owner)),
            ConfirmationPopup = true,
            Act = () =>
            {
                if (!_members.TryGetValue(user, out var still) || still.Owner != owner)
                    return;

                RemoveMember(user);
                _adminLog.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(body):player} left the members of {owner}");
                _popup.PopupEntity(Loc.GetString("persistent-poi-admin-members-left"), body, body);
            },
        });
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (GetOwner(ev.Player.UserId) is { } owner)
            Apply(ev.Mob, owner);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (GetOwner(ev.Player.UserId) is { } owner)
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
        if (_members.TryGetValue(user, out var member))
        {
            Apply(body, member.Owner);
            return PersistentTerritoryFactions.GetTeam(EntityManager, body) == member.Owner ? member.Owner : null;
        }

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
        if (_members.TryGetValue(user, out var member))
            return member.Owner;

        return _owners.GetValueOrDefault(user);
    }

    public NetUserId? GetFounder(string owner)
    {
        foreach (var (user, id) in _owners)
        {
            if (id == owner)
                return user;
        }

        return null;
    }

    public bool IsMember(NetUserId user)
    {
        return _members.ContainsKey(user);
    }

    public List<(NetUserId User, IndependentMember Member)> GetMembers(string owner)
    {
        var list = new List<(NetUserId, IndependentMember)>();
        foreach (var (user, member) in _members)
        {
            if (member.Owner == owner)
                list.Add((user, member));
        }

        list.Sort((a, b) => string.Compare(a.Item2.Name, b.Item2.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // founder or a co-owner
    public bool CanManage(NetUserId user, string owner)
    {
        if (_members.TryGetValue(user, out var member))
            return member.Owner == owner && member.CoOwner;

        return _owners.GetValueOrDefault(user) == owner;
    }

    public bool TryAddMember(EntityUid body, string owner)
    {
        if (!TryComp<ActorComponent>(body, out var actor))
            return false;

        var user = actor.PlayerSession.UserId;
        if (_members.ContainsKey(user) || _owners.GetValueOrDefault(user) == owner || GetMembers(owner).Count >= MaxMembers)
            return false;

        _members[user] = new IndependentMember { Owner = owner, Name = Name(body) };
        SaveMembers();
        Apply(body, owner);
        return true;
    }

    public void SetCoOwner(NetUserId user, bool coOwner)
    {
        if (!_members.TryGetValue(user, out var member) || member.CoOwner == coOwner)
            return;

        member.CoOwner = coOwner;
        SaveMembers();
    }

    public void RemoveMember(NetUserId user)
    {
        if (!_members.Remove(user, out var member))
            return;

        SaveMembers();

        if (!_players.TryGetSessionById(user, out var session) || session.AttachedEntity is not { } body)
            return;

        // their old group's guns stop letting them through
        var query = EntityQueryEnumerator<CaptureRegionDeviceComponent, NpcFactionMemberComponent, TransformComponent>();
        while (query.MoveNext(out var device, out _, out _, out var xform))
        {
            if (xform.GridUid is { } grid &&
                TryComp<PersistentPoiGridComponent>(grid, out var territory) &&
                territory.Owner == member.Owner)
            {
                _npcFactions.UnignoreEntity(device, body);
            }
        }

        if (_owners.TryGetValue(user, out var own))
            Apply(body, own);
        else
            RemComp<TerritoryIndependentComponent>(body);
    }

    private void Apply(EntityUid body, string owner)
    {
        var comp = EnsureComp<TerritoryIndependentComponent>(body);
        if (comp.Owner != owner)
        {
            comp.Owner = owner;
            Dirty(body, comp);
        }

        // not left out once the token is set: the faction may only turn IND after spawn, and the next call has to
        // still let them past their own guns
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

        LoadMembers();
    }

    private void LoadMembers()
    {
        _members.Clear();

        try
        {
            if (!_resources.UserData.Exists(MembersPath) || !_resources.UserData.TryReadAllText(MembersPath, out var json))
                return;

            var data = JsonSerializer.Deserialize<Dictionary<Guid, IndependentMember>>(json);
            foreach (var (user, member) in data ?? new())
            {
                if (PersistentTerritoryFactions.IsIndependent(member.Owner))
                    _members[new NetUserId(user)] = member;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load independent territory members: {e}");
        }
    }

    private void SaveMembers()
    {
        try
        {
            var data = new Dictionary<Guid, IndependentMember>();
            foreach (var (user, member) in _members)
                data[user.UserId] = member;

            _resources.UserData.CreateDir(MembersPath.Directory);
            _resources.UserData.WriteAllText(MembersPath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save independent territory members: {e}");
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

public sealed class IndependentMember
{
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool CoOwner { get; set; }
}
