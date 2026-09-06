using Content.Client.Storage.Visualizers;
using Content.Shared.Storage;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Storage;

[TestFixture]
public sealed class EntityStorageVisualsTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: TestLegacySuitStorageSprite
  parent: SuitStorageBase
  components:
  - type: Sprite
    layers:
    - state: base
    - state: door
      map: [enum.StorageVisualLayers.Door]
    - state: locked
      map: [enum.LockVisualLayers.Lock]
";

    [Test]
    public async Task LegacySpriteWithoutBaseKeyCanOpenAndClose()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var client = pair.Client;
        await client.WaitAssertion(() =>
        {
            var uid = client.EntMan.SpawnEntity("TestLegacySuitStorageSprite", Robust.Shared.Map.MapCoordinates.Nullspace);
            var sprite = client.EntMan.GetComponent<SpriteComponent>(uid);
            var appearance = client.System<AppearanceSystem>();
            Assert.That(sprite.LayerMapTryGet(StorageVisualLayers.Base, out _), Is.False);
            foreach (var open in new[] { false, true, false })
            {
                appearance.SetData(uid, StorageVisuals.Open, open);
                appearance.OnChangeData(uid, sprite);
                CheckLayer(sprite, StorageVisualLayers.Door, open ? "base" : "door", "legacy storage");
            }
            client.EntMan.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StorageSpritesSupportOpeningAndClosing()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var client = pair.Client;

        await client.WaitAssertion(() =>
        {
            var appearance = client.System<AppearanceSystem>();
            var count = 0;
            foreach (var proto in client.ProtoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract || pair.IsTestPrototype(proto)
                    || !proto.Components.ContainsKey("EntityStorageVisuals"))
                    continue;

                var uid = client.EntMan.SpawnEntity(proto.ID, Robust.Shared.Map.MapCoordinates.Nullspace);
                try
                {
                    var sprite = client.EntMan.GetComponent<SpriteComponent>(uid);
                    var visuals = client.EntMan.GetComponent<EntityStorageVisualsComponent>(uid);
                    foreach (var open in new[] { false, true, false })
                    {
                        appearance.SetData(uid, StorageVisuals.Open, open);
                        appearance.OnChangeData(uid, sprite);
                        CheckLayer(sprite, StorageVisualLayers.Base,
                            open ? visuals.StateBaseOpen : visuals.StateBaseClosed, proto.ID);
                        CheckLayer(sprite, StorageVisualLayers.Door,
                            open ? visuals.StateDoorOpen : visuals.StateDoorClosed, proto.ID);
                    }
                    count++;
                }
                finally
                {
                    client.EntMan.DeleteEntity(uid);
                }
            }
            Assert.That(count, Is.GreaterThan(0));
            TestContext.Out.WriteLine($"Checked opening and closing {count} storage prototypes.");
        });

        await pair.CleanReturnAsync();
    }

    private static void CheckLayer(SpriteComponent sprite, StorageVisualLayers key, string? expected, string id)
    {
        if (expected == null)
            return;

        Assert.That(sprite.LayerMapTryGet(key, out var index), Is.True, $"{id}: missing {key} layer");
        var layer = sprite[index];
        var rsi = layer.Rsi ?? sprite.BaseRSI;
        Assert.That(rsi, Is.Not.Null, $"{id}: no RSI for {key}");
        Assert.That(rsi!.TryGetState(expected, out _), Is.True, $"{id}: unknown state {expected} for {key}");
        Assert.That(layer.RsiState.ToString(), Is.EqualTo(expected), $"{id}: incorrect {key} state");
    }
}
