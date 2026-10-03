using Content.Server._Crescent.Religion.Components;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Shared._Crescent.HullrotFaction;
using Content.Shared._Crescent.Religion;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Verbs;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Religion;

/// <summary>
///     Gives players the faith from their profile at spawn, and runs the rites at altars that change it in round.
/// </summary>
public sealed partial class ReligionSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);

        SubscribeLocalEvent<ReligionAltarComponent, GetVerbsEvent<Verb>>(OnAltarVerbs);
        SubscribeLocalEvent<ReligionAltarComponent, ExaminedEvent>(OnAltarExamined);
        SubscribeLocalEvent<ReligionAltarComponent, ReligionRiteDoAfterEvent>(OnRiteStep);

        SubscribeLocalEvent<ReligionJoinRequestComponent, GetVerbsEvent<Verb>>(OnRequestVerbs);

        InitializePrayer();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ReligionJoinRequestComponent>();
        while (query.MoveNext(out var uid, out var request))
        {
            if (request.ExpiresAt > now)
                continue;

            _popup.PopupEntity(Loc.GetString("religion-request-expired"), uid, uid, PopupType.Medium);
            RemCompDeferred<ReligionJoinRequestComponent>(uid);
        }
    }

    #region Spawn

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        // Every humanoid body already carries one through its species prototype, holding no faith yet.
        var believer = EnsureComp<BelieverComponent>(args.Mob);
        believer.HomeFaction = args.Profile.Faction;

        // Command roles lead their faction's faith. A spacer-faithed Lord Admiral is not a thing.
        if (args.JobId != null
            && _proto.TryIndex<JobPrototype>(args.JobId, out var job)
            && job.RequiredReligion is { } required
            && _proto.TryIndex(required, out var office))
        {
            believer.Religion = office.ID;

            if (args.Profile.Religion != office.ID)
            {
                var notice = Loc.GetString("religion-spawn-office",
                    ("job", job.LocalizedName),
                    ("religion", Loc.GetString(office.Name)));
                _chatManager.ChatMessageToOne(ChatChannel.Server,
                    notice,
                    notice,
                    EntityUid.Invalid,
                    false,
                    args.Player.Channel,
                    office.Color);
            }

            return;
        }

        if (!_proto.TryIndex<ReligionPrototype>(args.Profile.Religion, out var religion))
            return;

        // The editor can only check against the job the player preferred, not the one they got.
        if (!CanHold(args.Mob, religion, args.JobId))
        {
            var message = Loc.GetString("religion-spawn-not-permitted", ("religion", Loc.GetString(religion.Name)));
            _chatManager.ChatMessageToOne(ChatChannel.Server,
                message,
                message,
                EntityUid.Invalid,
                false,
                args.Player.Channel,
                Color.Orange);
            return;
        }

        believer.Religion = religion.ID;
    }

    #endregion

    #region API

    public ProtoId<ReligionPrototype> GetReligion(EntityUid uid)
    {
        return CompOrNull<BelieverComponent>(uid)?.Religion ?? ReligionPrototype.Default;
    }

    public bool HoldsReligion(EntityUid uid, ProtoId<ReligionPrototype> religion)
    {
        return GetReligion(uid) == religion;
    }

    /// <summary>
    ///     Whether this faith is open to the character, through either their in-round faction or the faction they
    ///     spawned with, or through their job.
    /// </summary>
    public bool CanHold(EntityUid uid, ReligionPrototype religion, string? job = null)
    {
        if (job == null
            && _mind.TryGetMind(uid, out var mindId, out _)
            && _jobs.MindTryGetJobId(mindId, out var jobId))
        {
            job = jobId;
        }

        var faction = CompOrNull<HullrotFactionComponent>(uid)?.Faction;
        var home = CompOrNull<BelieverComponent>(uid)?.HomeFaction;

        return religion.IsOpenTo(faction, job) || religion.IsOpenTo(home, job);
    }

    /// <summary>
    ///     Whether the character's job requires a different faith than <paramref name="religion"/>. Only stops them
    ///     choosing it at an altar: a forced Binding still takes them, and is how a commander ends up a secret convert.
    /// </summary>
    public bool IsHeldByOffice(EntityUid uid, ProtoId<ReligionPrototype> religion, [NotNullWhen(true)] out string? reason)
    {
        reason = null;

        if (!_mind.TryGetMind(uid, out var mindId, out _)
            || !_jobs.MindTryGetJob(mindId, out var job)
            || job.RequiredReligion is not { } required
            || required == religion
            || !_proto.TryIndex(required, out var office))
            return false;

        reason = Loc.GetString("religion-office-bound",
            ("job", job.LocalizedName),
            ("religion", Loc.GetString(office.Name)));
        return true;
    }

    /// <summary>
    ///     Checks whether anything holds the character in their current faith.
    /// </summary>
    public bool CanChangeReligion(EntityUid uid, ProtoId<ReligionPrototype> religion, out string? reason)
    {
        var ev = new ReligionChangeAttemptEvent(religion);
        RaiseLocalEvent(uid, ref ev);
        reason = ev.Reason;
        return !ev.Cancelled;
    }

    /// <summary>
    ///     Changes a character's faith, skipping every faction and rite check.
    /// </summary>
    public void SetReligion(EntityUid uid,
        ProtoId<ReligionPrototype> religion,
        EntityUid? officiant = null,
        bool forced = false)
    {
        var believer = EnsureComp<BelieverComponent>(uid);
        var old = believer.Religion;
        believer.Religion = religion;

        var ev = new ReligionChangedEvent(old, religion, officiant, forced);
        RaiseLocalEvent(uid, ref ev);
    }

    /// <summary>
    ///     Says a rite line as the given character, in the language they are speaking.
    /// </summary>
    public void SpeakRiteLine(EntityUid speaker,
        LocId line,
        InGameICChatType type = InGameICChatType.Speak,
        bool ignoreActionBlocker = false)
    {
        _chat.TrySendInGameICMessage(speaker,
            Loc.GetString(line),
            type,
            ChatTransmitRange.Normal,
            ignoreActionBlocker: ignoreActionBlocker);
    }

    #endregion

    #region Admin

    /// <summary>
    ///     Lets an admin set anyone's faith straight from the context menu, for testing calls and conversions on
    ///     bodies nobody is playing. Called by <see cref="VeiledProphetSystem"/>, which owns the verb event on
    ///     <see cref="BelieverComponent"/>.
    /// </summary>
    public void AddAdminVerbs(EntityUid target, GetVerbsEvent<Verb> args)
    {
        if (!_admin.HasAdminFlag(args.User, AdminFlags.Fun))
            return;

        var category = new VerbCategory("religion-verb-category-admin", "/Textures/Interface/VerbIcons/group.svg.192dpi.png");
        var current = GetReligion(target);

        foreach (var religion in _proto.EnumeratePrototypes<ReligionPrototype>())
        {
            var id = religion.ID;
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString(religion.Name),
                Category = category,
                Priority = religion.Weight,
                Disabled = current == id,
                Act = () => SetReligion(target, id),
            });
        }
    }

    #endregion

    #region Altar

    private void OnAltarExamined(Entity<ReligionAltarComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange || !_proto.TryIndex(ent.Comp.Religion, out var religion))
            return;

        var name = Loc.GetString(religion.Name);
        var key = HoldsReligion(args.Examiner, religion.ID)
            ? "religion-altar-examine-follower"
            : religion.RequiresLeader
                ? "religion-altar-examine-leader"
                : "religion-altar-examine";

        args.PushMarkup(Loc.GetString(key, ("religion", name), ("color", religion.Color.ToHex())));
    }

    private void OnAltarVerbs(Entity<ReligionAltarComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess
            || !args.CanInteract
            || !HasComp<HumanoidAppearanceComponent>(args.User)
            || !_proto.TryIndex(ent.Comp.Religion, out var religion)
            || HoldsReligion(args.User, religion.ID))
            return;

        var user = args.User;
        var verb = new Verb
        {
            Text = Loc.GetString(religion.JoinVerb, ("religion", Loc.GetString(religion.Name))),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/group.svg.192dpi.png")),
            Act = () => TryJoin(user, ent, religion),
        };

        if (!CanHold(user, religion))
        {
            verb.Disabled = true;
            verb.Message = Loc.GetString("religion-not-permitted", ("religion", Loc.GetString(religion.Name)));
        }
        else if (IsHeldByOffice(user, religion.ID, out var office))
        {
            verb.Disabled = true;
            verb.Message = office;
        }
        else if (!CanChangeReligion(user, religion.ID, out var reason))
        {
            verb.Disabled = true;
            verb.Message = reason;
        }

        args.Verbs.Add(verb);
    }

    private void TryJoin(EntityUid user, Entity<ReligionAltarComponent> altar, ReligionPrototype religion)
    {
        if (!CanHold(user, religion)
            || IsHeldByOffice(user, religion.ID, out _)
            || !CanChangeReligion(user, religion.ID, out _))
            return;

        if (!religion.RequiresLeader)
        {
            BeginRite(user, altar, religion, null);
            return;
        }

        var leaders = GetLeadersNear(altar, religion.ID);
        if (leaders.Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("religion-request-no-leader", ("religion", Loc.GetString(religion.Name))),
                user,
                user,
                PopupType.MediumCaution);
            return;
        }

        var request = EnsureComp<ReligionJoinRequestComponent>(user);
        request.Altar = altar;
        request.Religion = religion.ID;
        request.ExpiresAt = _timing.CurTime + altar.Comp.RequestTimeout;

        _popup.PopupEntity(Loc.GetString("religion-request-sent"), user, user, PopupType.Medium);

        var leaderMessage = Loc.GetString("religion-request-leader",
            ("user", IdentityName(user)),
            ("religion", Loc.GetString(religion.Name)));

        foreach (var leader in leaders)
        {
            _popup.PopupEntity(leaderMessage, user, leader, PopupType.MediumCaution);

            if (!TryComp<ActorComponent>(leader, out var actor))
                continue;

            _chatManager.ChatMessageToOne(ChatChannel.Notifications,
                leaderMessage,
                leaderMessage,
                EntityUid.Invalid,
                false,
                actor.PlayerSession.Channel,
                religion.Color);
        }
    }

    private List<EntityUid> GetLeadersNear(Entity<ReligionAltarComponent> altar, ProtoId<ReligionPrototype> religion)
    {
        var leaders = new List<EntityUid>();
        foreach (var leader in _lookup.GetEntitiesInRange<ReligiousLeaderComponent>(Transform(altar).Coordinates,
                     altar.Comp.LeaderRange))
        {
            if (leader.Comp.Religion == religion && _mobState.IsAlive(leader))
                leaders.Add(leader);
        }

        return leaders;
    }

    private bool IsLeaderAtAltar(EntityUid leader, Entity<ReligionAltarComponent> altar, ProtoId<ReligionPrototype> religion)
    {
        return TryComp<ReligiousLeaderComponent>(leader, out var comp)
               && comp.Religion == religion
               && _mobState.IsAlive(leader)
               && _transform.InRange(Transform(leader).Coordinates, Transform(altar).Coordinates, altar.Comp.LeaderRange);
    }

    #endregion

    #region Leader approval

    private void OnRequestVerbs(Entity<ReligionJoinRequestComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess
            || !args.CanInteract
            || args.User == args.Target
            || ent.Comp.ExpiresAt <= _timing.CurTime
            || !TryComp<ReligionAltarComponent>(ent.Comp.Altar, out var altarComp)
            || !_proto.TryIndex(ent.Comp.Religion, out var religion))
            return;

        var altar = new Entity<ReligionAltarComponent>(ent.Comp.Altar, altarComp);
        if (!IsLeaderAtAltar(args.User, altar, religion.ID))
            return;

        var user = args.User;
        var target = ent.Owner;
        var name = Loc.GetString(religion.Name);

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("religion-verb-accept", ("religion", name)),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/group.svg.192dpi.png")),
            Priority = 2,
            Act = () =>
            {
                if (!TryComp<ReligionJoinRequestComponent>(target, out var request) || request.ExpiresAt <= _timing.CurTime)
                    return;

                RemComp<ReligionJoinRequestComponent>(target);

                if (!CanHold(target, religion)
                    || IsHeldByOffice(target, religion.ID, out _)
                    || !CanChangeReligion(target, religion.ID, out _))
                    return;

                BeginRite(target, altar, religion, user);
            },
        });

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("religion-verb-refuse", ("religion", name)),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/close.svg.192dpi.png")),
            Priority = 1,
            Act = () =>
            {
                if (!RemComp<ReligionJoinRequestComponent>(target))
                    return;

                _popup.PopupEntity(Loc.GetString("religion-request-refused", ("leader", IdentityName(user))),
                    target,
                    target,
                    PopupType.MediumCaution);
            },
        });
    }

    #endregion

    #region Rite

    private void BeginRite(EntityUid convert,
        Entity<ReligionAltarComponent> altar,
        ReligionPrototype religion,
        EntityUid? officiant)
    {
        if (!StartRiteStep(convert, altar, religion, 0, officiant))
            return;

        _popup.PopupEntity(Loc.GetString("religion-rite-begin", ("religion", Loc.GetString(religion.Name))),
            convert,
            convert,
            PopupType.Medium);

        SpeakStep(convert, religion, 0, officiant);
    }

    private bool StartRiteStep(EntityUid convert,
        Entity<ReligionAltarComponent> altar,
        ReligionPrototype religion,
        int step,
        EntityUid? officiant)
    {
        var args = new DoAfterArgs(EntityManager,
            convert,
            religion.RiteStepDelay,
            new ReligionRiteDoAfterEvent(step, GetNetEntity(officiant)),
            altar,
            target: altar)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            DistanceThreshold = altar.Comp.RiteRange,
        };

        return _doAfter.TryStartDoAfter(args);
    }

    private void SpeakStep(EntityUid convert, ReligionPrototype religion, int step, EntityUid? officiant)
    {
        if (step >= religion.Rite.Count)
            return;

        var line = religion.Rite[step];
        var speaker = line.Speaker == ReligionRiteSpeaker.Officiant && officiant != null
            ? officiant.Value
            : convert;

        SpeakRiteLine(speaker, line.Line);
    }

    private void OnRiteStep(Entity<ReligionAltarComponent> ent, ref ReligionRiteDoAfterEvent args)
    {
        if (args.Handled || !_proto.TryIndex(ent.Comp.Religion, out var religion))
            return;

        args.Handled = true;
        var convert = args.User;

        if (args.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString("religion-rite-interrupted"), convert, convert, PopupType.MediumCaution);
            return;
        }

        var officiant = GetEntity(args.Officiant);
        if (religion.RequiresLeader && (officiant == null || !IsLeaderAtAltar(officiant.Value, ent, religion.ID)))
        {
            _popup.PopupEntity(Loc.GetString("religion-rite-leader-left"), convert, convert, PopupType.MediumCaution);
            return;
        }

        if (!CanHold(convert, religion)
            || IsHeldByOffice(convert, religion.ID, out _)
            || !CanChangeReligion(convert, religion.ID, out _))
            return;

        var next = args.Step + 1;
        if (next < religion.Rite.Count)
        {
            if (StartRiteStep(convert, ent, religion, next, officiant))
                SpeakStep(convert, religion, next, officiant);

            return;
        }

        SetReligion(convert, religion.ID, officiant);

        if (religion.JoinedMessage is { } joined)
        {
            _popup.PopupEntity(Loc.GetString(joined), convert, convert, PopupType.Large);

            if (TryComp<ActorComponent>(convert, out var actor))
            {
                var message = Loc.GetString(joined);
                _chatManager.ChatMessageToOne(ChatChannel.Notifications,
                    message,
                    message,
                    EntityUid.Invalid,
                    false,
                    actor.PlayerSession.Channel,
                    religion.Color);
            }
        }
    }

    #endregion

    private string IdentityName(EntityUid uid)
    {
        return Identity.Name(uid, EntityManager);
    }
}
