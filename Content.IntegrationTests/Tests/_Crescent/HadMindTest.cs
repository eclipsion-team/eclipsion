using Content.Shared._Crescent.Mind;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
public sealed class HadMindTest
{
    [Test]
    public async Task DeletingAFormerPlayerBodyDoesNotMarkTerminatingLimbs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            var body = server.EntMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            server.EntMan.AddComponent<HadMindComponent>(body);
            server.EntMan.DeleteEntity(body);
            Assert.That(server.EntMan.EntityExists(body), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
