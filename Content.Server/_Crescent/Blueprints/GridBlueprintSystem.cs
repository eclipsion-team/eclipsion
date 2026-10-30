using System.IO;
using System.IO.Compression;
using System.Text;
using Content.Shared._Crescent.Blueprints;
using Content.Shared._Crescent.CCVar;
using Content.Shared.Ghost;
using Content.Shared.Mind.Components;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.Blueprints;

public sealed partial class GridBlueprintSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;

    private static readonly TimeSpan GlobalCooldown = TimeSpan.FromSeconds(10);

    private readonly Dictionary<NetUserId, TimeSpan> _nextAllowed = new();
    private TimeSpan _nextGlobal;

    private bool _enabled;
    private TimeSpan _cooldown;
    private int _maxSizeBytes;
    private bool _serializing;

    public override void Initialize()
    {
        base.Initialize();

        _mapLoader.OnIsSerializable += OnIsSerializable;

        SubscribeNetworkEvent<RequestGridBlueprintEvent>(OnRequest);

        Subs.CVar(_cfg, RatCCVars.GridBlueprintEnabled, value => _enabled = value, true);
        Subs.CVar(_cfg, RatCCVars.GridBlueprintCooldown, value => _cooldown = TimeSpan.FromSeconds(value), true);
        Subs.CVar(_cfg, RatCCVars.GridBlueprintMaxSizeKb, value => _maxSizeBytes = value * 1024, true);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _mapLoader.OnIsSerializable -= OnIsSerializable;
    }

    private void OnRequest(RequestGridBlueprintEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;

        if (!_enabled)
        {
            Fail(session, Loc.GetString("grid-blueprint-disabled"));
            return;
        }

        if (session.AttachedEntity is not { } player || HasComp<GhostComponent>(player))
        {
            Fail(session, Loc.GetString("grid-blueprint-no-body"));
            return;
        }

        if (Transform(player).GridUid is not { } grid || HasComp<MapComponent>(grid))
        {
            Fail(session, Loc.GetString("grid-blueprint-no-grid"));
            return;
        }

        var now = _timing.RealTime;
        if (_nextAllowed.TryGetValue(session.UserId, out var next) && next > now)
        {
            var seconds = (int) Math.Ceiling((next - now).TotalSeconds);
            Fail(session, Loc.GetString("grid-blueprint-cooldown", ("seconds", seconds)));
            return;
        }

        if (_nextGlobal > now)
        {
            var seconds = (int) Math.Ceiling((_nextGlobal - now).TotalSeconds);
            Fail(session, Loc.GetString("grid-blueprint-cooldown", ("seconds", seconds)));
            return;
        }

        _nextAllowed[session.UserId] = now + _cooldown;
        _nextGlobal = now + GlobalCooldown;

        if (!TrySerialize(grid, out var data))
        {
            Fail(session, Loc.GetString("grid-blueprint-failed"));
            return;
        }

        if (data.Length > _maxSizeBytes)
        {
            Fail(session, Loc.GetString("grid-blueprint-too-large"));
            return;
        }

        var name = MetaData(grid).EntityName;
        Log.Info($"{session.Name} downloaded a blueprint of {ToPrettyString(grid)} ({data.Length / 1024} KB).");
        RaiseNetworkEvent(new GridBlueprintEvent(name, data), session);
    }

    private bool TrySerialize(EntityUid grid, out byte[] data)
    {
        data = [];

        var options = SerializationOptions.Default with
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
        };

        var writer = new StringWriter();
        bool written;
        _serializing = true;
        try
        {
            written = _mapLoader.TrySaveGrid(grid, writer, options);
        }
        finally
        {
            _serializing = false;
        }

        if (!written)
            return false;

        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(writer.ToString());
            deflate.Write(bytes, 0, bytes.Length);
        }

        data = compressed.ToArray();
        return true;
    }

    private void Fail(ICommonSession session, string reason)
    {
        RaiseNetworkEvent(new GridBlueprintFailedEvent(reason), session);
    }

    private void OnIsSerializable(Entity<MetaDataComponent> ent, ref bool serializable)
    {
        if (_serializing && serializable && IsPlayerBound(ent))
            serializable = false;
    }

    private bool IsPlayerBound(EntityUid uid)
    {
        return HasComp<ActorComponent>(uid) ||
               HasComp<GhostComponent>(uid) ||
               TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind;
    }
}
