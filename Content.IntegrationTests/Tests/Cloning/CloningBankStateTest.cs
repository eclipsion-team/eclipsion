using Content.Server._Crescent.Bank;
using Content.Server.Cloning;
using Content.Server.Cloning.Components;
using Content.Server.Preferences.Managers;
using Content.Shared.Bank.Components;
using Content.Shared.CCVar;
using Content.Shared.Cloning;
using Content.Shared.Humanoid;
using Content.Shared.Mind;
using Content.Shared.Preferences;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Cloning;

[TestFixture]
public sealed class CloningBankStateTest
{
    [Test]
    public async Task AcceptedCloneUsesProfileBalanceAndTransfersHistory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IServerEntityManager>();
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        var userId = pair.Client.User!.Value;
        var oldAllowLiving = server.CfgMan.GetCVar(CCVars.CloningAllowLivingPeople);
        const long profileBalance = 777;
        const long runtimeBalance = 12345;

        await server.WaitAssertion(() =>
        {
            server.CfgMan.SetCVar(CCVars.CloningAllowLivingPeople, true);
            var preferences = prefs.GetPreferences(userId);
            var profile = ((HumanoidCharacterProfile) preferences.SelectedCharacter).WithBank(profileBalance);
            // Client profile edits cannot change money; seed the account through the server API.
            prefs.SetProfileNoChecks(userId, preferences.SelectedCharacterIndex, profile).Wait();
            Assert.That(((HumanoidCharacterProfile) prefs.GetPreferences(userId).SelectedCharacter).BankBalance,
                Is.EqualTo(profileBalance));

            var cloning = entMan.System<CloningSystem>();
            var minds = entMan.System<SharedMindSystem>();
            var source = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var pod = entMan.SpawnEntity("CloningPod", MapCoordinates.Nullspace);
            var console = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            entMan.AddComponent<CloningConsoleComponent>(console);

            var humanoid = entMan.GetComponent<HumanoidAppearanceComponent>(source);
            humanoid.LastProfileLoaded = profile;
            var podComponent = entMan.GetComponent<CloningPodComponent>(pod);
            podComponent.ConnectedConsole = console;
            podComponent.BiomassCostMultiplier = 0;

            entMan.AddComponent<BankAccountComponent>(source);
            var mind = minds.CreateMind(userId);
            minds.TransferTo(mind, source, mind: mind.Comp);
            entMan.GetComponent<BankAccountComponent>(source).Balance = runtimeBalance;
            entMan.AddComponent<BankTransferHistoryComponent>(source).Entries.Add(new BankTransferHistoryRecord
            {
                Outgoing = true,
                CounterpartyName = "Alice",
                Amount = 500,
                Comment = "Test transfer",
                RoundTimestamp = TimeSpan.FromMinutes(1),
            });

            Assert.That(cloning.TryCloning(pod, source, mind, podComponent), Is.True);
            var clone = cloning.ClonesWaitingForMind[mind.Comp];
            Assert.That(entMan.HasComponent<BankAccountComponent>(clone), Is.True,
                "The account must exist before mind transfer so BankSystem can load the profile balance.");

            cloning.TransferMindToClone(mind, mind.Comp);

            Assert.Multiple(() =>
            {
                Assert.That(mind.Comp.OwnedEntity, Is.EqualTo(clone));
                Assert.That(entMan.GetComponent<BankAccountComponent>(clone).Balance, Is.EqualTo(profileBalance));
                Assert.That(entMan.HasComponent<BankAccountComponent>(source), Is.False);
                Assert.That(entMan.HasComponent<BankTransferHistoryComponent>(source), Is.False);
            });

            var history = entMan.GetComponent<BankTransferHistoryComponent>(clone).Entries;
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(history[0].Outgoing, Is.True);
                Assert.That(history[0].CounterpartyName, Is.EqualTo("Alice"));
                Assert.That(history[0].Amount, Is.EqualTo(500));
                Assert.That(history[0].Comment, Is.EqualTo("Test transfer"));
                Assert.That(history[0].RoundTimestamp, Is.EqualTo(TimeSpan.FromMinutes(1)));
            });
        });

        server.CfgMan.SetCVar(CCVars.CloningAllowLivingPeople, oldAllowLiving);
        await pair.CleanReturnAsync();
    }
}
