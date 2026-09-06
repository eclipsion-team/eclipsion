using System.Linq;
using System.Net.Http;
using System.Text;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Content.Server.Procedural;
using Content.Shared.Bank.Components;
using Content.Server.GameTicking.Configuration;
using Content.Server.GameTicking.Events;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared.Procedural;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Map.Components;
using Content.Shared.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Cargo.Components;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Configuration;
using Content.Shared.Telescope;
using Robust.Shared.Utility;
using Content.Shared._Crescent.SpaceBiomes;

namespace Content.Server.GameTicking.Rules;

/// <summary>
/// This handles the dungeon and trading post spawning, as well as round end capitalism summary
/// </summary>
public sealed class NfAdventureRuleSystem : GameRuleSystem<AdventureRuleComponent>
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMapSystem _mapManager = default!;
    [Dependency] private readonly IConfigurationManager _configurationManager = default!;
    [Dependency] private readonly MapLoaderSystem _map = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly DungeonSystem _dunGen = default!;
    [Dependency] private readonly IConsoleHost _console = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;

    /// <summary>
    /// How many times a ring element is re-rolled while looking for a spot that clears everything else.
    /// </summary>
    private const int RingPlacementAttempts = 64;

    private readonly HttpClient _httpClient = new();
    private ISawmill _sawmill = default!;

    [ViewVariables]
    // this is used for money but its very poorly named - SPCR 2025
    private List<(EntityUid, long)> _players = new();

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("nfadventurerulesystem");

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawningEvent);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd);
    }

    private void OnRoundEnd(RoundEndMessageEvent args)
    {
        var query = EntityManager.EntityQueryEnumerator<SpaceBiomeSourceComponent>();
        while (query.MoveNext(out var uid, out var biomeSource))
        {
            biomeSource.Biome = "default";
        }
    }

    protected override void AppendRoundEndText(EntityUid uid, AdventureRuleComponent component, GameRuleComponent gameRule, ref RoundEndTextAppendEvent ev)
    {
        var profitText = Loc.GetString($"adventure-mode-profit-text");
        var lossText = Loc.GetString($"adventure-mode-loss-text");
        ev.AddLine(Loc.GetString("adventure-list-start"));
        var allScore = new List<Tuple<string, int>>();

        foreach (var player in _players)
        {
            if (!TryComp<BankAccountComponent>(player.Item1, out var bank) || !TryComp<MetaDataComponent>(player.Item1, out var meta))
                continue;

            var profit = (long) bank.Balance - player.Item2;
            allScore.Add(new Tuple<string, int>(meta.EntityName, (int) profit));
        }

        if (!(allScore.Count >= 1))
            return;

        // Sort by profit (highest first) and display all players
        var sortedScores = allScore.OrderByDescending(h => h.Item2).ToList();

        foreach (var score in sortedScores)
        {
            var displayText = score.Item2 < 0 ? lossText : profitText;
            ev.AddLine($"- {score.Item1} {displayText} {score.Item2} Credits");
        }
    }

    private void OnPlayerSpawningEvent(PlayerSpawnCompleteEvent ev)
    {
        if (ev.Player.AttachedEntity is { Valid: true } mobUid)
        {
            _players.Add((mobUid, ev.Profile.BankBalance));
            EnsureComp<CargoSellBlacklistComponent>(mobUid);

        }
    }

    /// <summary>
    /// This is a helper function that spawns in stuff by their gameMap .yml's ID field. The map's path is fetched from the gameMap .yml
    /// </summary>
    /// <param name="mapid"> the ID of the map. this is always GameTicker.DefaultMap; for hullrot </param>
    /// <param name="gameMapID">the ID of the gameMap prototype to spawn</param>
    /// <param name="position">the world position to spawn it at, already rolled by the caller</param>
    /// <param name="color">the IFF color to set this object to</param>
    /// <param name="IFFFaction">the IFF faction to set this to. i don't think this does anything</param>
    /// <param name="hideIFF">a boolean to set wether this is visible on the map screen or not</param>
    /// <param name="pinned">whether to nail the grid down so nothing can push it around</param>
    /// <param name="iffLabel">replaces the name the grid shows on radar, or null to keep the gameMap's own</param>
    private void SpawnMapElementByID(MapId mapid, string gameMapID, Vector2 position, Color color, string? iffFaction, bool hideIFF, bool pinned, string? iffLabel)
    {
        _sawmill.Info($"Attempting to spawn map element: {gameMapID} at ({position.X}, {position.Y})");
        if (_prototypeManager.TryIndex<GameMapPrototype>(gameMapID, out var stationProto))
        {
            if (_map.TryLoadGrid(mapid, new ResPath(stationProto.MapPath.ToString()), out var stationGridUid, null, position))
            {
                _station.InitializeNewStation(stationProto.Stations[gameMapID], [stationGridUid.Value.Owner]);

                // InitializeNewStation is what stamps the StationNameSetup name onto the grid, so the override
                // has to come after it. Only the grid is touched: the station entity keeps its real name for the
                // boarding announcement and for anyone reading the round from the admin side.
                if (iffLabel != null)
                    _meta.SetEntityName(stationGridUid.Value.Owner, iffLabel);

                // setting color if applicable. if not, White is default
                _shuttle.SetIFFColor(stationGridUid.Value.Owner, color);

                // set IFFFaction if applicable. dont know if this does anything
                if (iffFaction != null)
                    _shuttle.SetIFFFaction(stationGridUid.Value.Owner, iffFaction);

                // hide IFF if needed, like for derelicts or secrets
                if (hideIFF)
                    _shuttle.AddIFFFlag(stationGridUid.Value.Owner, IFFFlags.HideLabel);

                // Grids come out of the loader as enabled shuttles, so scenery drifts off the moment anything
                // touches it. Disable() is what the game already uses to park a shuttle: static, no rotation.
                if (pinned)
                    _shuttle.Disable(stationGridUid.Value.Owner);
            }
            else
            {
                _sawmill.Error($"Failed to load {gameMapID} in map {mapid}");
            }
        }
        else
        {
            _sawmill.Error($"GameMap prototype '{gameMapID}' not found!");
        }
    }

    protected override void Added(EntityUid uid, AdventureRuleComponent component, GameRuleComponent gameRule, GameRuleAddedEvent args)
    {
        base.Added(uid, component, gameRule, args);

        // Select this gamemode's worldgen layout. Preset rules are added inside StartGamePresetRules(),
        // which runs before RoundStartingEvent, so WorldgenConfigSystem picks up the new value when it
        // applies worldgen. Every AdventureRule sets this (defaulting to RatWorld), so it also resets
        // any override left over from a previous round.
        _configurationManager.SetCVar(CCVars.WorldgenConfig, component.WorldgenConfig);
        _sawmill.Info($"AdventureRule: worldgen config set to '{component.WorldgenConfig}' (worldgen.worldgen_config CVar).");
    }

    protected override void Started(EntityUid uid, AdventureRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        var mapId = GameTicker.DefaultMap;
        base.Started(uid, component, gameRule, args);

        _sawmill.Info($"AdventureRule Started for {uid}. GameMapsID count: {component.GameMapsID.Count}");

        // Every position is rolled before anything spawns, because a ring element only knows where it may land
        // once the fixed ones are down. Stations and box-offset elements therefore go first; the ring elements
        // then keep their MinClearance from everything already on the board, including each other.
        var placed = new List<Vector2>();
        var plan = new List<(HullrotMapElementGameMapID Element, Vector2 Position)>();

        foreach (var element in component.GameMapsID.Values)
        {
            if (element.RandomRingMax > 0f)
                continue;

            // These are independent positive axis offsets, not minimum/maximum vector magnitudes.
            var boxed = new Vector2(element.PositionX + _random.NextFloat(element.RandomOffsetX),
                                    element.PositionY + _random.NextFloat(element.RandomOffsetY));
            placed.Add(boxed);
            plan.Add((element, boxed));
        }

        foreach (var element in component.GameMapsID.Values)
        {
            if (element.RandomRingMax <= 0f)
                continue;

            var rolled = RollRingPosition(element, placed);
            placed.Add(rolled);
            plan.Add((element, rolled));
        }

        foreach (var (element, position) in plan)
        {
            SpawnMapElementByID(mapId,
                                element.GameMapID,
                                position,
                                element.IFFColor,
                                element.IFFFaction,
                                element.HideIFF,
                                element.Pinned,
                                element.IFFLabel);
        }
    }

    /// <summary>
    /// Rolls a point on the element's ring around posX/posY, then re-rolls until it clears everything already
    /// placed this round. The radius is interpolated on r^2 so rolls spread evenly over the ring's area instead
    /// of bunching against its inner edge.
    /// </summary>
    /// <remarks>
    /// A ring that cannot be satisfied keeps its last roll rather than failing to spawn: a wreck sitting a bit
    /// too close to something is a far smaller problem than a wreck missing from the round entirely.
    /// </remarks>
    private Vector2 RollRingPosition(HullrotMapElementGameMapID element, List<Vector2> placed)
    {
        var center = new Vector2(element.PositionX, element.PositionY);
        var max = element.RandomRingMax;
        var min = MathF.Min(MathF.Max(element.RandomRingMin, 0f), max);
        var clearanceSquared = element.MinClearance * element.MinClearance;
        var position = center;

        for (var attempt = 0; attempt < RingPlacementAttempts; attempt++)
        {
            var angle = _random.NextFloat(MathF.Tau);
            var radius = MathF.Sqrt(min * min + _random.NextFloat() * (max * max - min * min));
            position = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

            if (element.MinClearance <= 0f)
                return position;

            var clear = true;
            foreach (var other in placed)
            {
                if ((other - position).LengthSquared() >= clearanceSquared)
                    continue;

                clear = false;
                break;
            }

            if (clear)
                return position;
        }

        _sawmill.Warning(
            $"No clear ring spot for {element.GameMapID} after {RingPlacementAttempts} rolls; keeping the last one.");
        return position;
    }
}
