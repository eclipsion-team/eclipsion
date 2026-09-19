using System.Numerics;
using Content.Server._Crescent.Barricades;
using Content.Server._Crescent.Psionics;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.Abilities.Psionics;

/// <summary>
/// Projects a four-tile, wall-bounded cone of persistent Crescent/RMC floor fire.
/// </summary>
/// <remarks>
/// The cone does not appear all at once. Tiles are grouped into bands by how far down the cone they
/// sit and lit one band at a time, so the fire visibly runs out from the caster's feet to the far
/// edge over about half a second rather than the whole four tiles igniting on the same frame.
/// </remarks>
public sealed class PsionicFlameBreathPowerSystem : EntitySystem
{
    private const float ConeLength = 4.5f;
    private const float ConeHalfAngleTangent = 0.5f;

    /// <summary>
    /// Gap between one band of the cone lighting and the next.
    /// </summary>
    private static readonly TimeSpan WaveInterval = TimeSpan.FromSeconds(0.15);

    [Dependency] private readonly CrescentTileFireSystem _tileFire = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly PsionicNullifierSystem _nullifier = default!;
    [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// Cones still travelling outwards. Held on the system rather than on an entity: the fire it
    /// leaves behind is the lasting part, and a half-second of travel does not need a component.
    /// </summary>
    private readonly List<PendingBreath> _pending = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PsionicFlameBreathActionEvent>(OnFlameBreath);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        var now = _timing.CurTime;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var breath = _pending[i];

            // A round restart rewinds CurTime, which would otherwise park the next band in the
            // future and leave a half-lit cone sitting in the list forever.
            if (breath.NextWave > now + WaveInterval)
                breath.NextWave = now;

            if (now < breath.NextWave)
                continue;

            if (!TryComp<MapGridComponent>(breath.Grid, out var grid))
            {
                _pending.RemoveAt(i);
                continue;
            }

            foreach (var tile in breath.Bands[breath.Band])
                TrySpawnFire(breath.Grid, grid, tile);

            breath.Band++;
            breath.NextWave = now + WaveInterval;

            if (breath.Band >= breath.Bands.Count)
                _pending.RemoveAt(i);
        }
    }

    private void OnFlameBreath(PsionicFlameBreathActionEvent args)
    {
        if (args.Handled
            || !_psionics.OnAttemptPowerUse(args.Performer, "flame breath", true)
            || !TryComp<MapGridComponent>(Transform(args.Performer).GridUid, out var grid))
            return;

        var gridUid = Transform(args.Performer).GridUid!.Value;
        var originMap = _transform.GetMapCoordinates(args.Performer);
        var targetMap = _transform.ToMapCoordinates(args.Target);
        if (originMap.MapId != targetMap.MapId)
            return;

        var origin = _map.MapToGrid(gridUid, originMap);
        var target = _map.MapToGrid(gridUid, targetMap);
        var direction = target.Position - origin.Position;
        if (direction.LengthSquared() < 0.01f)
            return;

        direction = Vector2.Normalize(direction);
        var originTile = _map.TileIndicesFor(gridUid, grid, origin);
        var extent = (int) MathF.Ceiling(ConeLength);

        // One band per whole metre of reach. Index zero is the tile at the caster's feet.
        var bands = new List<List<Vector2i>>();
        for (var band = 0; band <= extent; band++)
            bands.Add(new List<Vector2i>());

        for (var x = -extent; x <= extent; x++)
            for (var y = -extent; y <= extent; y++)
            {
                var candidate = originTile + new Vector2i(x, y);
                var coordinates = _map.GridTileToLocal(gridUid, grid, candidate);
                var delta = coordinates.Position - origin.Position;
                var forward = Vector2.Dot(delta, direction);
                var lateral = MathF.Abs(delta.X * direction.Y - delta.Y * direction.X);

                if (forward < 0.35f
                    || forward > ConeLength
                    || lateral > MathF.Max(0.45f, forward * ConeHalfAngleTangent)
                    || IsOccluded(gridUid, grid, originTile, candidate))
                    continue;

                bands[Math.Clamp((int) forward, 0, bands.Count - 1)].Add(candidate);
            }

        bands.RemoveAll(band => band.Count == 0);
        if (bands.Count == 0)
            return;

        // The first band lights immediately, so the power always does something on the frame it is
        // pressed even if the caster is shot the instant afterwards.
        foreach (var tile in bands[0])
            TrySpawnFire(gridUid, grid, tile);

        if (bands.Count > 1)
        {
            _pending.Add(new PendingBreath
            {
                Grid = gridUid,
                Bands = bands,
                Band = 1,
                NextWave = _timing.CurTime + WaveInterval,
            });
        }

        var visual = Spawn("EffectPsionicFlameBreath", Transform(args.Performer).Coordinates);
        _transform.SetParent(visual, args.Performer);
        _transform.SetLocalPosition(visual, direction * 0.55f);
        _transform.SetLocalRotation(visual, direction.ToWorldAngle());

        _psionics.LogPowerUsed(args.Performer, "flame breath", 5, 8);
        args.Handled = true;
    }

    /// <summary>
    /// The flames are the power itself, so they will not catch on a tile inside a null field.
    /// </summary>
    private void TrySpawnFire(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        if (_nullifier.IsInsideNullField(_transform.ToMapCoordinates(_map.GridTileToLocal(gridUid, grid, tile))))
            return;

        _tileFire.TrySpawnTileFire(gridUid, grid, tile);
    }

    private bool IsOccluded(
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i origin,
        Vector2i destination)
    {
        var delta = destination - origin;
        var steps = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
        for (var step = 1; step < steps; step++)
        {
            var progress = (float) step / steps;
            var indices = new Vector2i(
                (int) MathF.Round(origin.X + delta.X * progress),
                (int) MathF.Round(origin.Y + delta.Y * progress));

            if (_tileFire.IsFireBlocked(gridUid, grid, indices))
                return true;
        }

        return false;
    }

    /// <summary>
    /// One cone still working its way outwards, a band of tiles at a time.
    /// </summary>
    private sealed class PendingBreath
    {
        public EntityUid Grid;
        public List<List<Vector2i>> Bands = new();
        public int Band;
        public TimeSpan NextWave;
    }
}
