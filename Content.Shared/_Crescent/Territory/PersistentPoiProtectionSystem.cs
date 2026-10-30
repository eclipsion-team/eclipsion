using Content.Shared.Construction.Components;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Robust.Shared.Network;

namespace Content.Shared._Crescent.Territory;

public sealed class PersistentPoiProtectionSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PersistentPoiStructureComponent, UnanchorAttemptEvent>(OnStructureUnanchor);
        SubscribeLocalEvent<PersistentPoiStructureComponent, ExaminedEvent>(OnStructureExamined);
    }

    private void OnStructureUnanchor(Entity<PersistentPoiStructureComponent> ent, ref UnanchorAttemptEvent args)
    {
        args.Cancel();

        if (_net.IsServer)
            _popup.PopupEntity(Loc.GetString("persistent-poi-structure-unanchor-denied"), ent, args.User, PopupType.SmallCaution);
    }

    private void OnStructureExamined(Entity<PersistentPoiStructureComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("persistent-poi-structure-examine"));
    }
}
