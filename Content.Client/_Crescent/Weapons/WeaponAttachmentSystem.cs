using Content.Shared._Crescent.Weapons;
using Robust.Client.GameObjects;

namespace Content.Client._Crescent.Weapons;

public sealed class WeaponAttachmentSystem : SharedWeaponAttachmentSystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WeaponAttachmentComponent, AfterAutoHandleStateEvent>(OnAfterAutoHandleStateEvent);
    }

    protected override void AddSharp(EntityUid uid) { }
    protected override void RemSharp(EntityUid uid) { }

    private void OnAfterAutoHandleStateEvent(EntityUid uid, WeaponAttachmentComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        SetLayerVisible(sprite, WeaponVisualLayers.Bayonet, component.BayonetAttached);
        SetLayerVisible(sprite, WeaponVisualLayers.FlightOff, component.LightAttached && !component.LightOn);
        SetLayerVisible(sprite, WeaponVisualLayers.FlightOn, component.LightAttached && component.LightOn);
    }

    // Not every gun that takes attachments draws every attachment layer (the sawn-off PKA has none).
    private static void SetLayerVisible(SpriteComponent sprite, WeaponVisualLayers key, bool visible)
    {
        if (sprite.LayerMapTryGet(key, out var layer))
            sprite.LayerSetVisible(layer, visible);
    }
}