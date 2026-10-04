using Content.Server.Cargo.Systems;
using Content.Server.GameTicking.Events;
using Content.Server.Storage.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Mobs.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Economy;

/// <summary>
/// Keeps lathe-printable goods from selling for more than the materials they are printed from.
/// Without this, a rifle printed from a few hundred spesos of steel sold to cargo at its full
/// static price, turning any lathe into a money printer.
/// </summary>
/// <remarks>
/// The cap is a per-prototype price multiplier rather than a tag on printed instances, so rounds
/// pulled out of a printed ammo box are covered. Stacks are capped by stack type, so printed units
/// split off or merged into a looted stack of another prototype are covered too. The multiplier is
/// shared by every instance, looted or not: anything a lathe can make is never worth more than
/// making it. Reagents and gases are never scaled, as they can be poured or pumped out into any
/// container; a recipe whose product holds more of them than its materials cost is reported.
/// </remarks>
public sealed class ManufacturedPriceCapSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly EconomyPriceSystem _economyPrice = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    /// <summary>
    /// Material use multiplier of a lathe with fully upgraded parts. A product may sell for no more
    /// than the materials such a lathe spends on it, the same assumption MaterialArbitrageTest makes.
    /// </summary>
    public static readonly float UpgradedLatheMaterialMultiplier =
        MathF.Pow(LatheComponent.DefaultPartRatingMaterialUseMultiplier, MachinePartComponent.MaxRating - 1);

    /// <summary>
    /// Price multiplier per entity prototype id. Prototypes that are not capped are absent.
    /// Built at round start, since it spawns every printable product once, or on the first
    /// appraisal if that comes sooner.
    /// </summary>
    private Dictionary<string, double>? _multipliers;

    /// <summary>
    /// The most one unit of a stack type may sell for, for stack types a lathe prints. Built
    /// alongside <see cref="_multipliers"/>. Kept per unit rather than as a multiplier, since
    /// prototypes sharing a stack type can carry different unit prices.
    /// </summary>
    private Dictionary<string, double>? _stackUnitCaps;

    /// <summary>
    /// Set while the table is being built, so the appraisals it makes come out uncapped.
    /// </summary>
    private bool _building;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<EntityPrototype>()
            || args.WasModified<LatheRecipePrototype>()
            || args.WasModified<MaterialPrototype>())
        {
            _multipliers = null;
            _stackUnitCaps = null;
        }
    }

    /// <summary>
    /// Building the table spawns every printable product. Done here, with the round still loading, rather than
    /// in the middle of a round on the first appraisal, which could also land inside a loop over entities.
    /// </summary>
    private void OnRoundStarting(RoundStartingEvent args)
    {
        EnsureMultipliers();
    }

    /// <summary>
    /// Returns the factor an entity's own price is scaled by, or 1 if it is not capped.
    /// An admin price override is an explicit decision and is never capped.
    /// </summary>
    /// <param name="price">The entity's own price before capping, contents and reagents left out.</param>
    public double GetMultiplier(EntityUid uid, double price)
    {
        if (_building || MetaData(uid).EntityPrototype?.ID is not { } protoId
            || _economyPrice.TryGetItemOverride(protoId, out _))
            return 1;

        EnsureMultipliers();

        var multiplier = _multipliers!.GetValueOrDefault(protoId, 1);
        if (price > 0
            && TryComp<StackComponent>(uid, out var stack)
            && TryGetStackUnitCap(stack.StackTypeId, out var unitCap))
        {
            multiplier = Math.Min(multiplier, unitCap * stack.Count / price);
        }

        return multiplier;
    }

    /// <summary>
    /// The most one unit of a stack type may sell for, if a lathe prints it.
    /// </summary>
    public bool TryGetStackUnitCap(string stackType, out double unitCap)
    {
        EnsureMultipliers();
        unitCap = 0;
        return _stackUnitCaps?.TryGetValue(stackType, out unitCap) == true;
    }

    private void EnsureMultipliers()
    {
        if (_building || (_multipliers != null && _stackUnitCaps != null))
            return;

        (_multipliers, _stackUnitCaps) = BuildMultipliers();
    }

    /// <summary>
    /// Whether a recipe's product is covered by the cap. Refined materials are skipped: ore
    /// processors are lathes too, and refining is the mining economy's intended value-add.
    /// Mobs are skipped as cargo refuses to sell them.
    /// </summary>
    public bool IsCappedRecipe(LatheRecipePrototype recipe)
    {
        return !recipe.Abstract
               && recipe.Result is { } result
               && _prototypeManager.TryIndex(result, out var proto)
               && !IsMaterial(proto)
               && !proto.Components.ContainsKey(_factory.GetComponentName(typeof(MobStateComponent)));
    }

    /// <summary>
    /// The most a recipe's product may sell for, in spesos.
    /// </summary>
    public double GetRecipeSaleCap(LatheRecipePrototype recipe)
    {
        var value = 0.0;
        foreach (var (material, amount) in recipe.Materials)
        {
            if (!_prototypeManager.TryIndex(material, out var materialProto))
                continue;

            var spent = SharedLatheSystem.AdjustMaterial(amount, recipe.ApplyMaterialDiscount, UpgradedLatheMaterialMultiplier);
            value += materialProto.Price * spent;
        }

        return value;
    }

    private bool IsMaterial(EntityPrototype proto)
    {
        return proto.Components.ContainsKey(_factory.GetComponentName(typeof(MaterialComponent)));
    }

    private (Dictionary<string, double> Prototypes, Dictionary<string, double> Stacks) BuildMultipliers()
    {
        // Several recipes can print the same thing; the cheapest one sets its worth.
        var caps = new Dictionary<string, double>();
        foreach (var recipe in _prototypeManager.EnumeratePrototypes<LatheRecipePrototype>())
        {
            if (!IsCappedRecipe(recipe))
                continue;

            var result = recipe.Result!.Value.Id;
            var cap = GetRecipeSaleCap(recipe);
            if (!caps.TryGetValue(result, out var existing) || cap < existing)
                caps[result] = cap;
        }

        var multipliers = new Dictionary<string, double>();
        var stackUnitCaps = new Dictionary<string, double>();
        var members = new List<string>();

        // Products are appraised as actually spawned, on an initialized map so they come with
        // everything they normally start with: magazines, hardsuit helmets, machine boards...
        _building = true;
        var mapUid = _map.CreateMap(out var mapId);
        try
        {
            var coords = new MapCoordinates(0, 0, mapId);
            foreach (var (result, cap) in caps)
            {
                EntityUid ent;
                try
                {
                    ent = Spawn(result, coords);
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to spawn {result} to appraise it: {e}");
                    continue;
                }

                double total;
                double movable;
                int? stackCount = null;
                string? stackType = null;
                members.Clear();
                try
                {
                    total = _pricing.GetPrice(ent, true, out movable);
                    CollectMembers(ent, members);

                    if (TryComp<StackComponent>(ent, out var stack) && stack.Count > 0)
                    {
                        stackCount = stack.Count;
                        stackType = stack.StackTypeId;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to appraise {result}: {e}");
                    continue;
                }
                finally
                {
                    Del(ent);
                }

                // Units of a printed stack sell for no more than they cost to print, whichever
                // prototype of that stack type they end up in once split off or merged.
                if (stackType != null && stackCount is { } count)
                {
                    var unitCap = Math.Max(0, cap - movable) / count;
                    if (!stackUnitCaps.TryGetValue(stackType, out var existingUnitCap) || unitCap < existingUnitCap)
                        stackUnitCaps[stackType] = unitCap;
                }

                if (total <= cap)
                    continue;

                // Scale the product and everything it comes with by the same factor, so taking the
                // magazine out of a printed gun, or the rounds out of a printed box, gains nothing.
                // Reagents and gases are left out of it: they sell the same in any container.
                var factor = 0.0;
                var scaled = total - movable;
                if (movable >= cap)
                {
                    Log.Warning($"{result} comes with {movable} spesos of reagents or gas, but its cheapest recipe costs {cap}. Printing it is free money; make its recipe dearer.");
                }
                else if (scaled > 0)
                {
                    factor = (cap - movable) / scaled;
                }

                foreach (var member in members)
                {
                    if (!multipliers.TryGetValue(member, out var existing) || factor < existing)
                        multipliers[member] = factor;
                }
            }
        }
        finally
        {
            Del(mapUid);
            _building = false;
        }

        Log.Info($"Capped the sale price of {multipliers.Count} lathe-printable prototypes and {stackUnitCaps.Count} stack types.");
        return (multipliers, stackUnitCaps);
    }

    /// <summary>
    /// Collects the prototypes of an entity and everything it contains, including rounds and
    /// spawn-on-use items that are only priced, not yet spawned. Refined materials are left out:
    /// their prices belong to the mining economy and must not be dragged down globally.
    /// </summary>
    private void CollectMembers(EntityUid uid, List<string> members)
    {
        if (MetaData(uid).EntityPrototype is { } proto)
            AddMember(proto.ID, members);

        if (TryComp<BallisticAmmoProviderComponent>(uid, out var ammo) && ammo.Proto is { } round)
            AddMember(round, members);

        if (TryComp<SpawnItemsOnUseComponent>(uid, out var spawnOnUse))
        {
            foreach (var entry in spawnOnUse.Items)
            {
                if (entry.PrototypeId is { } id)
                    AddMember(id, members);
            }
        }

        if (!TryComp<ContainerManagerComponent>(uid, out var containers))
            return;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
            {
                CollectMembers(contained, members);
            }
        }
    }

    private void AddMember(string protoId, List<string> members)
    {
        if (_prototypeManager.TryIndex<EntityPrototype>(protoId, out var proto) && !IsMaterial(proto))
            members.Add(protoId);
    }
}
