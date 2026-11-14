using System.Globalization;
using Content.Server._Crescent.Translator;
using Content.Shared.CCVar;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server.Popups
{
    public sealed class PopupSystem : SharedPopupSystem
    {
        [Dependency] private readonly IPlayerManager _player = default!;
        [Dependency] private readonly IConfigurationManager _cfg = default!;
        [Dependency] private readonly INetConfigurationManager _netCfg = default!;
        [Dependency] private readonly ILocalizationManager _loc = default!;
        [Dependency] private readonly TransformSystem _xform = default!;
        [Dependency] private readonly RelocalizeSystem _relocalize = default!;

        private readonly Dictionary<string, CultureInfo?> _cultures = new();

        public override void PopupCursor(string? message, PopupType type = PopupType.Small)
        {
            // No local user.
        }

        public override void PopupCursor(string? message, ICommonSession recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            Send(message, m => new PopupCursorEvent(m, type), recipient);
        }

        public override void PopupCursor(string? message, EntityUid recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            if (TryComp(recipient, out ActorComponent? actor))
                Send(message, m => new PopupCursorEvent(m, type), actor.PlayerSession);
        }

        public override void PopupCoordinates(string? message, EntityCoordinates coordinates, Filter filter, bool replayRecord, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var net = GetNetCoordinates(coordinates);
            Send(message, m => new PopupCoordinatesEvent(m, type, net), filter, replayRecord);
        }

        public override void PopupCoordinates(string? message, EntityCoordinates coordinates, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var mapPos = coordinates.ToMap(EntityManager, _xform);
            var filter = Filter.Empty().AddPlayersByPvs(mapPos, entManager: EntityManager, playerMan: _player, cfgMan: _cfg);
            var net = GetNetCoordinates(coordinates);
            Send(message, m => new PopupCoordinatesEvent(m, type, net), filter);
        }

        public override void PopupCoordinates(string? message, EntityCoordinates coordinates, ICommonSession recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var net = GetNetCoordinates(coordinates);
            Send(message, m => new PopupCoordinatesEvent(m, type, net), recipient);
        }

        public override void PopupCoordinates(string? message, EntityCoordinates coordinates, EntityUid recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            if (TryComp(recipient, out ActorComponent? actor))
            {
                var net = GetNetCoordinates(coordinates);
                Send(message, m => new PopupCoordinatesEvent(m, type, net), actor.PlayerSession);
            }
        }

        public override void PopupEntity(string? message, EntityUid uid, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var filter = Filter.Empty().AddPlayersByPvs(uid, entityManager: EntityManager, playerMan: _player, cfgMan: _cfg);
            var net = GetNetEntity(uid);
            Send(message, m => new PopupEntityEvent(m, type, net), filter);
        }

        public override void PopupEntity(string? message, EntityUid uid, EntityUid recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            if (TryComp(recipient, out ActorComponent? actor))
            {
                var net = GetNetEntity(uid);
                Send(message, m => new PopupEntityEvent(m, type, net), actor.PlayerSession);
            }
        }

        public override void PopupClient(string? message, EntityUid? recipient, PopupType type = PopupType.Small)
        {
        }

        public override void PopupClient(string? message, EntityUid uid, EntityUid? recipient, PopupType type = PopupType.Small)
        {
            // do nothing duh its for client only
        }

        public override void PopupClient(string? message, EntityCoordinates coordinates, EntityUid? recipient, PopupType type = PopupType.Small)
        {
        }

        public override void PopupEntity(string? message, EntityUid uid, ICommonSession recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var net = GetNetEntity(uid);
            Send(message, m => new PopupEntityEvent(m, type, net), recipient);
        }

        public override void PopupEntity(string? message, EntityUid uid, Filter filter, bool recordReplay, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var net = GetNetEntity(uid);
            Send(message, m => new PopupEntityEvent(m, type, net), filter, recordReplay);
        }

        public override void PopupPredicted(string? message, EntityUid uid, EntityUid? recipient, PopupType type = PopupType.Small)
        {
            if (message == null)
                return;

            var net = GetNetEntity(uid);
            if (recipient != null)
            {
                // Don't send to recipient, since they predicted it locally
                var filter = Filter.PvsExcept(recipient.Value, entityManager: EntityManager);
                Send(message, m => new PopupEntityEvent(m, type, net), filter);
            }
            else
            {
                // With no recipient, send to everyone (in PVS range)
                Send(message, m => new PopupEntityEvent(m, type, net), Filter.Broadcast());
            }
        }

        public override void PopupPredicted(string? recipientMessage, string? othersMessage, EntityUid uid, EntityUid? recipient, PopupType type = PopupType.Small)
        {
            PopupPredicted(othersMessage, uid, recipient, type);
        }

        // Eclipsion - players with another language get the popup rewritten in theirs
        private CultureInfo? CultureOf(ICommonSession session)
        {
            var name = _netCfg.GetClientCVar(session.Channel, CCVars.PlayerLanguage);
            if (name == _loc.DefaultCulture?.Name)
                return null;

            if (_cultures.TryGetValue(name, out var culture))
                return culture;

            // name is whatever the client sent, don't keep ones we don't have
            if (Array.IndexOf(Content.Shared.Localizations.ContentLocalizationManager.Languages, name) < 0)
                return null;

            culture = new CultureInfo(name);
            _cultures[name] = culture;
            return culture;
        }

        private string Localize(string message, CultureInfo? culture)
        {
            if (culture == null)
                return message;

            return _relocalize.TryRelocalize(message, culture, out var text) ? text : message;
        }

        private void Send(string message, Func<string, EntityEventArgs> make, ICommonSession recipient)
        {
            RaiseNetworkEvent(make(Localize(message, CultureOf(recipient))), recipient);
        }

        private void Send(string message, Func<string, EntityEventArgs> make, Filter filter, bool recordReplay = true)
        {
            Dictionary<CultureInfo, Filter>? others = null;
            foreach (var session in filter.Recipients)
            {
                if (CultureOf(session) is not { } culture)
                    continue;

                others ??= new();
                if (!others.TryGetValue(culture, out var group))
                    others[culture] = group = Filter.Empty();
                group.AddPlayer(session);
            }

            if (others == null)
            {
                RaiseNetworkEvent(make(message), filter, recordReplay);
                return;
            }

            // the replay keeps the original text
            var rest = filter.Clone();
            foreach (var group in others.Values)
                rest.RemovePlayers(group.Recipients);
            RaiseNetworkEvent(make(message), rest, recordReplay);

            foreach (var (culture, group) in others)
                RaiseNetworkEvent(make(Localize(message, culture)), group, false);
        }
    }
}
