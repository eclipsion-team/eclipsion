using System.Numerics;
using Content.Server.Power.Generator;
using Content.Shared._Crescent.RepairStation;
using Content.Shared.Decals;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Containers;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Maps;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.RepairStation;

public sealed partial class ShipRepairStationSystem
{
    /// <summary>
    /// Per-survey price memos, so a wreck missing four hundred identical wall segments prices that
    /// prototype once. Cleared at the start of every survey so quotes track material prices.
    /// </summary>
    private readonly Dictionary<int, int> _palettePrices = new();
    private readonly Dictionary<int, int> _tilePrices = new();
    private readonly Dictionary<string, int> _protoPrices = new();
    private readonly Dictionary<string, float> _protoSurcharges = new();

    /// <summary>
    /// Structures this survey has already written a heal line for. Two snapshots and a walk of the deck
    /// can all arrive at the same wall, and the customer is billed for beating it out once.
    /// </summary>
    private readonly HashSet<EntityUid> _healed = new();

    /// <summary>
    /// What one pass over a hull turned up: the work to do, and what it costs the customer.
    /// </summary>
    private struct ShipRepairSurvey
    {
        public List<ShipRepairJob> Jobs;
        public int MissingTiles;
        public int StrippedTiles;
        public int MissingParts;
        public int DamagedParts;
        public int Decals;
        public int Spills;
        public int Debris;
        public int Restocks;

        /// <summary>
        /// Price with the yard's markup already applied.
        /// </summary>
        public int Quote;
    }

    /// <summary>
    /// Compares a docked hull against its snapshots and against the state of what is still aboard.
    /// Structures that are gone get rebuilt, ones still standing get beaten back into shape, decking
    /// blown off its plating gets laid again, spills get mopped, the wreckage the hull shed gets
    /// binned, and anything running short of ammunition or fuel gets topped up. A tile swapped for a
    /// different one, or a wall a crewman took down on purpose, is left alone rather than billed for,
    /// and so is anything he has already put back himself.
    /// </summary>
    private ShipRepairSurvey Survey(Entity<ShipRepairStationComponent> station, EntityUid ship, ShipRepairDataComponent? data)
    {
        var survey = new ShipRepairSurvey { Jobs = new List<ShipRepairJob>() };

        if (!TryComp<MapGridComponent>(ship, out var gridComp))
            return survey;

        _palettePrices.Clear();
        _tilePrices.Clear();
        _protoPrices.Clear();
        _protoSurcharges.Clear();
        _healed.Clear();

        var standing = BuildOccupancy(ship);
        var raw = 0L;

        // Everything a hull is missing is read back out of its blueprint, and a hull nobody ever filed
        // one for - a station, a fleet ship that was mapped in rather than bought - simply has none of
        // that half of the survey. What is gone from it is gone without trace.
        if (data != null)
            SurveyBlueprint(station, ship, gridComp, data, standing, ref survey, ref raw);

        var drydock = CompOrNull<ShipDrydockSnapshotComponent>(ship);
        if (drydock != null)
        {
            foreach (var part in drydock.Parts)
            {
                if (StillStanding(part, ship))
                {
                    TryClaim(standing, part.Tile, part.Proto, out _);
                    AddHealJob(station, ref survey, ref raw, part.Original!.Value, part.Tile);
                    continue;
                }

                if (TryClaim(standing, part.Tile, part.Proto, out var present))
                {
                    AddHealJob(station, ref survey, ref raw, present, part.Tile);
                    continue;
                }

                var drydockPrice = GetStructurePrice(part.Proto);
                survey.Jobs.Add(new ShipRepairJob
                {
                    Kind = ShipRepairJobKind.DrydockPart,
                    Indices = part.Tile,
                    Part = part,
                    Cost = drydockPrice,
                });

                survey.MissingParts++;
                raw += drydockPrice;
            }

            SurveyDecals(station, ship, gridComp, drydock, ref survey, ref raw);
        }

        // Without a blueprint there is nothing to compare the hull against, so the damage still standing on it
        // is found by walking the deck instead. Only a blueprint retires that pass: it is the one record that
        // covers the whole hull, and the loop above already claimed and beat out every structure on it. A
        // drydock snapshot is not the same thing - it is the list of parts the slip captured, so anything
        // outside it (fitted since, or never captured) would go unquoted on a hull nobody filed a blueprint
        // for. AddHealJob dedupes on _healed, so the overlap between the two passes costs the customer nothing.
        SurveyAboard(station, ship, gridComp, data == null, ref survey, ref raw);

        // Descending, because the job list is popped from the back. That makes the yard work its way
        // up the hull instead of jumping about, and settles the order within a tile: the plating, then
        // any weapon hardpoint, then everything else. A gun only looks for its mount on the tick it
        // spawns, so putting the gun back first would leave it sitting on nothing.
        survey.Jobs.Sort(static (a, b) => JobOrder(b).CompareTo(JobOrder(a)));

        survey.Quote = (int) Math.Min(int.MaxValue, MathF.Ceiling(raw * station.Comp.PriceMarkup));
        return survey;
    }

    /// <summary>
    /// The half of the survey read back out of the hull's own blueprint: the plating it has lost, and
    /// the structures the hand-held device would weld back. Structures that are still standing are
    /// claimed off the occupancy map as they are found, so the drydock pass after this one does not
    /// quote for them a second time.
    /// </summary>
    private void SurveyBlueprint(
        Entity<ShipRepairStationComponent> station,
        EntityUid ship,
        MapGridComponent gridComp,
        ShipRepairDataComponent data,
        Dictionary<(Vector2i Tile, string Proto), List<EntityUid>> standing,
        ref ShipRepairSurvey survey,
        ref long raw)
    {
        var size = data.ChunkSize;

        foreach (var (chunkIndices, chunk) in data.Chunks)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var stored = chunk.Tiles[x + y * size];
                    if (stored == Tile.Empty.TypeId)
                        continue;

                    var indices = new Vector2i(chunkIndices.X * size + x, chunkIndices.Y * size + y);
                    var current = _map.GetTileRef(ship, gridComp, indices).Tile.TypeId;
                    if (current == stored)
                        continue;

                    // Open to space, or beaten down to one of the layers underneath - decking blown
                    // off leaves the plating it was laid on, and the slip covers that back over. A
                    // tile that is neither is one a crewman chose to lay, and is his business.
                    var breached = current == Tile.Empty.TypeId;
                    if (!breached && !IsWornDownTo(stored, current))
                        continue;

                    var tilePrice = GetTilePrice(station, stored);
                    survey.Jobs.Add(new ShipRepairJob
                    {
                        Kind = ShipRepairJobKind.Tile,
                        Indices = indices,
                        Breach = breached,
                        Cost = tilePrice,
                    });

                    if (breached)
                        survey.MissingTiles++;
                    else
                        survey.StrippedTiles++;
                    raw += tilePrice;
                }
            }

            foreach (var (specId, spec) in chunk.Entities)
            {
                if (spec.ProtoIndex < 0 || spec.ProtoIndex >= data.EntityPalette.Count)
                    continue;

                var indices = _map.LocalToTile(ship, gridComp, new EntityCoordinates(ship, spec.LocalPosition));

                // Something of this kind already stands there, whether the original or a crewman's
                // replacement. Claiming it stops the slip stacking a second one on the tile, and it
                // is then a candidate for having its damage beaten out instead.
                if (TryClaim(standing, indices, data.EntityPalette[spec.ProtoIndex], out var present))
                {
                    AddHealJob(station, ref survey, ref raw, present, indices);
                    continue;
                }

                if (!IsSpecMissing(spec))
                    continue;

                var partPrice = GetPartPrice(data, spec.ProtoIndex);
                survey.Jobs.Add(new ShipRepairJob
                {
                    Kind = ShipRepairJobKind.ToolPart,
                    Indices = indices,
                    SpecId = specId,
                    Cost = partPrice,
                });

                survey.MissingParts++;
                raw += partPrice;
            }
        }
    }

    /// <summary>
    /// Compares the markings painted on the deck against the ones on file. A decal dies with the
    /// plating it was laid on, so a rebuilt compartment would otherwise come back bare.
    /// </summary>
    private void SurveyDecals(
        Entity<ShipRepairStationComponent> station,
        EntityUid ship,
        MapGridComponent gridComp,
        ShipDrydockSnapshotComponent drydock,
        ref ShipRepairSurvey survey,
        ref long raw)
    {
        if (drydock.Decals.Count == 0)
            return;

        // Paint needs a deck under it. Tiles the slip is about to lay count, since the marking goes
        // down straight after them, but a marking over a hole the blueprint leaves open cannot be
        // restored at all and is not worth quoting for.
        var relaid = new HashSet<Vector2i>();
        foreach (var job in survey.Jobs)
        {
            if (job.Kind == ShipRepairJobKind.Tile)
                relaid.Add(job.Indices);
        }

        var present = BuildDecalCounts(ship);

        foreach (var decal in drydock.Decals)
        {
            var key = DecalKey(decal);

            // Claimed one by one, so a tile that wore three identical stripes and kept two is quoted
            // for the one it lost rather than for none of them.
            if (present.TryGetValue(key, out var count) && count > 0)
            {
                present[key] = count - 1;
                continue;
            }

            var tile = DecalTile(decal);
            if (_map.GetTileRef(ship, gridComp, tile).Tile.IsEmpty && !relaid.Contains(tile))
                continue;

            survey.Jobs.Add(new ShipRepairJob
            {
                Kind = ShipRepairJobKind.Decal,
                Indices = tile,
                Decal = decal,
                Cost = station.Comp.DecalPaintFee,
            });

            survey.Decals++;
            raw += station.Comp.DecalPaintFee;
        }
    }

    /// <summary>
    /// How many of each marking the hull is wearing right now.
    /// </summary>
    private Dictionary<(Vector2, string, Color?, double, int), int> BuildDecalCounts(EntityUid grid)
    {
        var counts = new Dictionary<(Vector2, string, Color?, double, int), int>();

        if (!TryComp<DecalGridComponent>(grid, out var decals))
            return counts;

        foreach (var (_, chunk) in decals.ChunkCollection.ChunkCollection)
        {
            foreach (var (_, decal) in chunk.Decals)
            {
                var key = DecalKey(decal);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// What makes two markings the same one. Compared exactly rather than loosely, because a marking
    /// that survived is the very object the snapshot holds - the fields cannot have drifted.
    /// </summary>
    private static (Vector2, string, Color?, double, int) DecalKey(Decal decal)
    {
        return (decal.Coordinates, decal.Id, decal.Color, decal.Angle.Theta, decal.ZIndex);
    }

    /// <summary>
    /// The tile a marking sits on. Decal coordinates are the corner of the tile they were painted on.
    /// </summary>
    private static Vector2i DecalTile(Decal decal)
    {
        return new Vector2i((int) MathF.Floor(decal.Coordinates.X), (int) MathF.Floor(decal.Coordinates.Y));
    }

    /// <summary>
    /// Whether this exact marking is already on the deck, asked again at the moment of work because a
    /// crewman may have repainted it himself.
    /// </summary>
    private bool HasDecal(EntityUid grid, Decal decal)
    {
        if (!TryComp<DecalGridComponent>(grid, out var decals))
            return false;

        if (!decals.ChunkCollection.ChunkCollection.TryGetValue(SharedDecalSystem.GetChunkIndices(decal.Coordinates), out var chunk))
            return false;

        var key = DecalKey(decal);
        foreach (var (_, candidate) in chunk.Decals)
        {
            if (DecalKey(candidate) == key)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Everything that is judged by looking at the ship rather than at a snapshot: puddles on the
    /// deck, wreckage lying about, guns below capacity, generators run dry.
    /// </summary>
    private void SurveyAboard(
        Entity<ShipRepairStationComponent> station,
        EntityUid ship,
        MapGridComponent gridComp,
        bool healUnfiled,
        ref ShipRepairSurvey survey,
        ref long raw)
    {
        var children = Transform(ship).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (TerminatingOrDeleted(child))
                continue;

            var xform = Transform(child);
            var tile = _map.LocalToTile(ship, gridComp, xform.Coordinates);

            if (HasComp<PuddleComponent>(child))
            {
                survey.Jobs.Add(new ShipRepairJob
                {
                    Kind = ShipRepairJobKind.Clean,
                    Indices = tile,
                    Target = child,
                    Cost = station.Comp.SpillCleaningFee,
                });

                survey.Spills++;
                raw += station.Comp.SpillCleaningFee;
                continue;
            }

            // Lying loose on the deck. Either it is wreckage the hull shed on its way to pieces - the
            // sheets off a broken wall, the shards off a window, the coil a cut cable left behind -
            // which gets binned so the customer is not handed his ship back whole but knee deep in
            // the old one's remains, or it is his property and none of the slip's business. Nothing
            // loose is topped up either, or a crate of magazines would make the slip an ammo vendor.
            if (!xform.Anchored)
            {
                if (_drydock.IsDebris(child))
                {
                    survey.Jobs.Add(new ShipRepairJob
                    {
                        Kind = ShipRepairJobKind.Sweep,
                        Indices = tile,
                        Target = child,
                        Cost = station.Comp.DebrisClearingFee,
                    });

                    survey.Debris++;
                    raw += station.Comp.DebrisClearingFee;
                }

                continue;
            }

            // Nothing on file to compare this hull against, so anything the yard would have had on
            // file - the structures the device welds, plus everything the slip's own scope takes - is
            // beaten back into shape where it stands. Mobs are not in either, and stay out of it.
            if (healUnfiled && (HasComp<ShipRepairableComponent>(child) || _drydock.InScope(child)))
                AddHealJob(station, ref survey, ref raw, child, tile);

            if (!NeedsResupply(station, child, out var resupply))
                continue;

            survey.Jobs.Add(new ShipRepairJob
            {
                Kind = ShipRepairJobKind.Restock,
                Indices = tile,
                Target = child,
                Cost = resupply,
            });

            survey.Restocks++;
            raw += resupply;
        }
    }

    /// <summary>
    /// Bills for beating the damage out of a structure that survived. Nothing is charged for a
    /// structure that is already sound.
    /// </summary>
    private void AddHealJob(
        Entity<ShipRepairStationComponent> station,
        ref ShipRepairSurvey survey,
        ref long raw,
        EntityUid target,
        Vector2i tile)
    {
        if (!TryComp<DamageableComponent>(target, out var damage) || damage.TotalDamage <= FixedPoint2.Zero)
            return;

        if (!_healed.Add(target))
            return;

        var healPrice = (int) MathF.Ceiling(damage.TotalDamage.Float()
                                           * station.Comp.DamageRepairRate
                                           * GetSurcharge(MetaData(target).EntityPrototype?.ID));
        survey.Jobs.Add(new ShipRepairJob
        {
            Kind = ShipRepairJobKind.Heal,
            Indices = tile,
            Target = target,
            Cost = healPrice,
        });

        survey.DamagedParts++;
        raw += healPrice;
    }

    /// <summary>
    /// Whether this thing is running short of anything the slip refills, and what filling it costs.
    /// </summary>
    /// <remarks>
    /// The need and the price are two separate questions. Judging need by price alone skipped every gun
    /// and generator whose ammunition or fuel carries no market price - which is most of them - so the
    /// line was quoted and the tank stayed empty.
    /// </remarks>
    private bool NeedsResupply(Entity<ShipRepairStationComponent> station, EntityUid uid, out int cost)
    {
        cost = 0;
        var needed = false;

        if (TryComp<BasicEntityAmmoProviderComponent>(uid, out var basic)
            && basic.Capacity is { } basicCap
            && basic.Count is { } basicCount
            && basicCount < basicCap)
        {
            needed = true;
            cost += (basicCap - basicCount) * GetRoundPrice(station, basic.Proto);
        }

        if (TryComp<BallisticAmmoProviderComponent>(uid, out var ballistic)
            && ballistic.Proto is { } ballisticProto
            && ballistic.Count < ballistic.Capacity)
        {
            needed = true;
            cost += (ballistic.Capacity - ballistic.Count) * GetRoundPrice(station, ballisticProto);
        }

        if (TryGetFuelDeficit(uid, out _, out var deficit, out var reagent))
        {
            needed = true;
            cost += (int) MathF.Ceiling(deficit.Float() * GetReagentPrice(station, reagent));
        }

        return needed;
    }

    /// <summary>
    /// What one round costs, falling back to a flat figure for ammunition nothing has priced.
    /// </summary>
    private int GetRoundPrice(Entity<ShipRepairStationComponent> station, string protoId)
    {
        var price = GetProtoPrice(protoId);
        return price > 0 ? price : station.Comp.FallbackRoundPrice;
    }

    /// <summary>
    /// How much fuel a generator is short, and what it burns. Only generators declaring a chemical
    /// fuel adapter are considered, so no beaker or water tank aboard gets topped up by accident.
    /// </summary>
    private bool TryGetFuelDeficit(
        EntityUid uid,
        out Entity<SolutionComponent> solutionEnt,
        out FixedPoint2 deficit,
        out string reagent)
    {
        solutionEnt = default;
        deficit = FixedPoint2.Zero;
        reagent = string.Empty;

        if (!TryComp<ChemicalFuelGeneratorAdapterComponent>(uid, out var adapter) || adapter.Reagents.Count == 0)
            return false;

        if (!_solution.TryGetSolution(uid, adapter.SolutionName, out var found, out var solution))
            return false;

        if (solution.AvailableVolume <= FixedPoint2.Zero)
            return false;

        // Refill whatever it is already running on, falling back to the first fuel it accepts.
        reagent = adapter.Reagents.Keys.First().Id;
        foreach (var (candidate, _) in adapter.Reagents)
        {
            if (solution.GetTotalPrototypeQuantity(candidate.Id) <= FixedPoint2.Zero)
                continue;

            reagent = candidate.Id;
            break;
        }

        solutionEnt = found.Value;
        deficit = solution.AvailableVolume;
        return true;
    }

    /// <summary>
    /// What one unit of fuel costs, falling back to a flat figure for the many reagents that carry no
    /// market price at all.
    /// </summary>
    private float GetReagentPrice(Entity<ShipRepairStationComponent> station, string reagent)
    {
        var price = _proto.TryIndex<ReagentPrototype>(reagent, out var proto) ? proto.PricePerUnit : 0f;
        return price > 0f ? price : station.Comp.FallbackReagentPrice;
    }

    /// <summary>
    /// Work order within one tile: the tile itself, then its markings, then weapon hardpoints, then
    /// other structures, then the jobs that only touch what is already aboard. Paint goes on before
    /// the walls, because a marking cannot be laid on a tile that is still open to space.
    /// </summary>
    private static int JobRank(ShipRepairJob job)
    {
        return job.Kind switch
        {
            ShipRepairJobKind.Tile => 0,
            ShipRepairJobKind.Decal => 1,
            ShipRepairJobKind.DrydockPart when job.Part is { IsMount: true } => 2,
            ShipRepairJobKind.ToolPart or ShipRepairJobKind.DrydockPart => 3,
            _ => 4,
        };
    }

    private static (int Y, int X, int Rank) JobOrder(ShipRepairJob job)
    {
        return (job.Indices.Y, job.Indices.X, JobRank(job));
    }

    /// <summary>
    /// Lists what is anchored on each tile of the hull right now, by prototype. Surveying claims
    /// against this so two snapshot entries never both point at the same standing structure.
    /// </summary>
    private Dictionary<(Vector2i Tile, string Proto), List<EntityUid>> BuildOccupancy(EntityUid grid)
    {
        var standing = new Dictionary<(Vector2i, string), List<EntityUid>>();

        if (!TryComp<MapGridComponent>(grid, out var gridComp))
            return standing;

        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            var xform = Transform(child);
            if (!xform.Anchored || MetaData(child).EntityPrototype is not { } proto)
                continue;

            var key = (_map.LocalToTile(grid, gridComp, xform.Coordinates), proto.ID);
            if (!standing.TryGetValue(key, out var list))
                standing[key] = list = new List<EntityUid>();

            list.Add(child);
        }

        return standing;
    }

    /// <summary>
    /// Whether the structure the slip recorded is still the thing it recorded. The prototype is
    /// checked alongside the reference so a stale handle cannot pass for a part that is really gone.
    /// </summary>
    private bool StillStanding(DrydockPart part, EntityUid grid)
    {
        return part.Original is { } original
               && !TerminatingOrDeleted(original)
               && Transform(original).GridUid == grid
               && MetaData(original).EntityPrototype?.ID == part.Proto.Id;
    }

    private static bool TryClaim(
        Dictionary<(Vector2i, string), List<EntityUid>> standing,
        Vector2i tile,
        string proto,
        out EntityUid claimed)
    {
        claimed = EntityUid.Invalid;

        if (!standing.TryGetValue((tile, proto), out var list) || list.Count == 0)
            return false;

        claimed = list[^1];
        list.RemoveAt(list.Count - 1);
        return true;
    }

    /// <summary>
    /// True when something of this prototype is already anchored on the tile, checked again at the
    /// moment of work because a crewman may have beaten the slip to it.
    /// </summary>
    private bool TileHolds(EntityUid grid, MapGridComponent gridComp, Vector2i tile, string proto)
    {
        foreach (var ent in _map.GetAnchoredEntities(grid, gridComp, tile))
        {
            if (MetaData(ent).EntityPrototype?.ID == proto)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Clears the wreckage of the structure about to be put back. A wall beaten past its first
    /// threshold leaves a girder standing on the tile; without this the slip welds a new wall straight
    /// through it and the tile ends up holding both. What counts as wreckage is listed in the scope
    /// files, and only bare frames belong there.
    /// </summary>
    private void ClearRemnants(EntityUid grid, MapGridComponent gridComp, Vector2i tile, string protoId)
    {
        foreach (var ent in _map.GetAnchoredEntities(grid, gridComp, tile))
        {
            if (TerminatingOrDeleted(ent) || MetaData(ent).EntityPrototype?.ID == protoId)
                continue;

            if (_drydock.IsClearable(ent))
                QueueDel(ent);
        }
    }

    /// <summary>
    /// Whether the tile now on the deck is one of the layers beneath what the blueprint recorded.
    /// Explosions and prying take a tile down its own base-turf chain a layer at a time - dark tiles
    /// to plating, plating to lattice - and only that counts as damage. Any other tile standing there
    /// is one someone laid on purpose.
    /// </summary>
    private bool IsWornDownTo(int stored, int current)
    {
        var id = stored;

        // Nothing stacks more than a handful of layers deep; the bound is only here so a base-turf
        // loop in yaml cannot hang the survey.
        for (var depth = 0; depth < 8; depth++)
        {
            if (!_tileDefs.TryGetDefinition(id, out var def)
                || def is not ContentTileDefinition content
                || string.IsNullOrEmpty(content.BaseTurf)
                || !_tileDefs.TryGetDefinition(content.BaseTurf, out var below))
            {
                return false;
            }

            if (below.TileId == current)
                return true;

            id = below.TileId;
        }

        return false;
    }

    private int GetTilePrice(Entity<ShipRepairStationComponent> station, int tileId)
    {
        if (_tilePrices.TryGetValue(tileId, out var cached))
            return cached;

        var price = station.Comp.FallbackTilePrice;

        if (_tileDefs[tileId] is ContentTileDefinition def
            && _proto.TryIndex<EntityPrototype>(def.ItemDropPrototypeName, out var dropProto))
        {
            var estimate = (int) _pricing.GetEstimatedPrice(dropProto);
            if (estimate > 0)
                price = estimate;
        }

        _tilePrices[tileId] = price;
        return price;
    }

    private int GetPartPrice(ShipRepairDataComponent data, int paletteIndex)
    {
        if (_palettePrices.TryGetValue(paletteIndex, out var cached))
            return cached;

        var price = paletteIndex >= 0 && paletteIndex < data.EntityPalette.Count
            ? GetStructurePrice(data.EntityPalette[paletteIndex])
            : 0;

        _palettePrices[paletteIndex] = price;
        return price;
    }

    /// <summary>
    /// What the yard charges to put one structure back - what it is worth in parts and fittings, plus
    /// the premium on the classes of part that take more than a welder to fit.
    /// </summary>
    private int GetStructurePrice(string protoId)
    {
        return (int) MathF.Ceiling(GetProtoPrice(protoId) * GetSurcharge(protoId));
    }

    /// <summary>
    /// The premium on this class of part, memoed for the survey because a wreck missing forty
    /// identical mounts asks the same question forty times.
    /// </summary>
    private float GetSurcharge(string? protoId)
    {
        if (protoId == null)
            return 1f;

        if (_protoSurcharges.TryGetValue(protoId, out var cached))
            return cached;

        var multiplier = _proto.TryIndex<EntityPrototype>(protoId, out var proto)
            ? _drydock.GetSurcharge(proto)
            : 1f;

        _protoSurcharges[protoId] = multiplier;
        return multiplier;
    }

    private int GetProtoPrice(string protoId)
    {
        if (_protoPrices.TryGetValue(protoId, out var cached))
            return cached;

        var price = 0;
        if (_proto.TryIndex<EntityPrototype>(protoId, out var proto))
            price = (int) _pricing.GetEstimatedPrice(proto) + GetFillPrice(proto, 0);

        _protoPrices[protoId] = price;
        return price;
    }

    /// <summary>
    /// What a structure's factory-fitted contents are worth. Pricing deliberately ignores what is inside
    /// a thing, and most fitted machinery is worth more than its shell - an airlock is a frame around an
    /// electronics board - so quoting the shell alone would sell the customer the board for nothing.
    /// </summary>
    /// <remarks>
    /// Only what the prototype ships with is counted. Anything a crewman put in there himself went with
    /// the wreck and is not reinstated, so it is not billed for either.
    /// </remarks>
    private int GetFillPrice(EntityPrototype proto, int depth)
    {
        // A fitted part can be a filled thing in its own right; the bound is only here so a prototype
        // that somehow contains itself cannot hang the survey.
        if (depth >= 3 || !proto.TryGetComponent<ContainerFillComponent>(out var fill, _compFactory))
            return 0;

        var price = 0;
        foreach (var (_, contents) in fill.Containers)
        {
            foreach (var contained in contents)
            {
                if (!_proto.TryIndex<EntityPrototype>(contained, out var containedProto))
                    continue;

                price += (int) _pricing.GetEstimatedPrice(containedProto) + GetFillPrice(containedProto, depth + 1);
            }
        }

        return price;
    }
}
