using Content.Server.Humanoid.Systems;
using Content.Shared.Dataset;
using Content.Shared.Humanoid;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Crescent.NPC;

public sealed class NpcFactionNameSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NpcFactionNameComponent, MapInitEvent>(OnMapInit,
            after: [typeof(RandomHumanoidAppearanceSystem)]);
    }

    private void OnMapInit(Entity<NpcFactionNameComponent> ent, ref MapInitEvent args)
    {
        _meta.SetEntityName(ent, MakeName(ent));
    }

    public string MakeName(Entity<NpcFactionNameComponent> ent)
    {
        var female = CompOrNull<HumanoidAppearanceComponent>(ent)?.Sex switch
        {
            Sex.Female => true,
            Sex.Male => false,
            _ => _random.Prob(0.5f),
        };

        var first = Pick(female ? ent.Comp.FemaleFirst : ent.Comp.MaleFirst);
        var last = Pick(ent.Comp.Last);

        var name = ent.Comp.Nicknames is { } nicks && _random.Prob(ent.Comp.NicknameChance)
            ? Loc.GetString("npc-faction-name-nickname", ("first", first), ("nick", Pick(nicks)), ("last", last))
            : Loc.GetString("npc-faction-name", ("first", first), ("last", last));

        if (ent.Comp.Ranks.Count > 0)
            name = Loc.GetString("npc-faction-name-rank", ("rank", Loc.GetString(_random.Pick(ent.Comp.Ranks))), ("name", name));

        return name;
    }

    private string Pick(ProtoId<DatasetPrototype> dataset)
    {
        return _proto.TryIndex(dataset, out var proto) && proto.Values.Count > 0
            ? _random.Pick(proto.Values)
            : string.Empty;
    }
}
