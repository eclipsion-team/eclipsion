using System.Collections.Generic;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.Sprite;

[TestFixture]
public sealed class GenericVisualizerStateTest
{
    [Test]
    public async Task AppearanceStatesExistInTheirRsi()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var client = pair.Client;
        var errors = new List<string>();
        var checkedStates = 0;
        await client.WaitAssertion(() =>
        {
            var resources = client.ResolveDependency<IResourceCache>();
            var reflection = client.ResolveDependency<IReflectionManager>();
            foreach (var proto in client.ProtoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract || pair.IsTestPrototype(proto)
                    || !proto.Components.ContainsKey("GenericVisualizer")
                    || !proto.Components.ContainsKey("Sprite"))
                    continue;

                var uid = client.EntMan.SpawnEntity(proto.ID, MapCoordinates.Nullspace);
                try
                {
                    var sprite = client.EntMan.GetComponent<SpriteComponent>(uid);
                    var visual = client.EntMan.GetComponent<GenericVisualizerComponent>(uid);
                    foreach (var (_, layers) in visual.Visuals)
                    foreach (var (rawKey, values) in layers)
                    foreach (var (value, data) in values)
                    {
                        if (data.State == null)
                            continue;

                        object key = reflection.TryParseEnumReference(rawKey, out var parsed) ? parsed : rawKey;
                        var rsi = sprite.LayerMapTryGet(key, out var index)
                            ? sprite[index].Rsi ?? sprite.BaseRSI
                            : sprite.BaseRSI;
                        if (data.RsiPath != null)
                        {
                            // TryGetResource, not GetResource: a visualizer naming an RSI that does not exist at
                            // all is one more line in the report, not an exception that aborts the sweep at the
                            // first bad prototype and hides every other broken reference behind it.
                            var path = new ResPath("/Textures") / data.RsiPath;
                            if (!resources.TryGetResource<RSIResource>(path, out var resource))
                            {
                                errors.Add($"{proto.ID}: {rawKey}={value} references missing RSI '{path}'.");
                                checkedStates++;
                                continue;
                            }

                            rsi = resource.RSI;
                        }

                        if (rsi == null || !rsi.TryGetState(data.State, out _))
                            errors.Add($"{proto.ID}: {rawKey}={value} references missing state '{data.State}' in {rsi?.Path}.");
                        checkedStates++;
                    }
                }
                finally
                {
                    client.EntMan.DeleteEntity(uid);
                }
            }
        });
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(checkedStates, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"Checked {checkedStates} appearance states.");
        await pair.CleanReturnAsync();
    }
}
