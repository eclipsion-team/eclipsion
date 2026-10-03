using Content.Shared._Crescent.Contractors.Components;
using Content.Shared._Crescent.Contractors.Systems;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Crescent;

[TestFixture]
[TestOf(typeof(PassportComponent))]
public sealed class PassportTest
{
    [Test]
    public async Task ProfileSelectionsArePrintedAndRecorded()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var passportSystem = entMan.System<SharedPassportSystem>();
            var passport = entMan.SpawnEntity("NCWLPassport", map.GridCoords);
            var component = entMan.GetComponent<PassportComponent>(passport);
            var profile = new HumanoidCharacterProfile
            {
                Employer = "NanoTrasen",
                Lifepath = "DeskWorker",
            };

            passportSystem.UpdatePassportProfile(new(passport, component), profile);

            Assert.Multiple(() =>
            {
                // Prototype ids are converted to the same player-facing names used by the profile editor.
                Assert.That(component.Employer, Is.EqualTo("Shinohara Heavy Industries"));
                Assert.That(component.Lifepath, Is.EqualTo("Desk Worker"));
                Assert.That(component.Record, Is.Not.Null);
                Assert.That(component.Record!.Employer, Is.EqualTo(component.Employer));
                Assert.That(component.Record.Lifepath, Is.EqualTo(component.Lifepath));
                Assert.That(component.IsClosed, Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The forgery minigame rests on a single invariant: the editor rewrites what a passport
    /// reads but can never reach the issuer's record, so a checker printout still shows the
    /// identity the document was issued with and the discrepancy stays findable by hand.
    /// </summary>
    [Test]
    public async Task EditingPassportLeavesIssuerRecordIntact()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            entMan.System<SharedPassportSystem>();
            var passport = entMan.SpawnEntity("NCWLPassport", map.GridCoords);
            var actor = entMan.SpawnEntity(null, map.GridCoords);
            var component = entMan.GetComponent<PassportComponent>(passport);

            component.FullName = "Test Person";
            component.Age = 30;
            component.Species = "Human";
            component.Sex = "Female";
            component.HeightCm = 170;
            component.Nationality = "Test Nation";
            component.Employer = "Test Employer";
            component.Lifepath = "Test Lifepath";
            component.PassportId = "ABCDE-FGHIJ-KLMNO";
            component.IssueYear = 2450;
            component.ExpirationYear = 2455;
            component.Record = new PassportRecord
            {
                FullName = component.FullName,
                Age = component.Age,
                Species = component.Species,
                Sex = component.Sex,
                HeightCm = component.HeightCm,
                Nationality = component.Nationality,
                Employer = component.Employer,
                Lifepath = component.Lifepath,
                PassportId = component.PassportId,
                IssueYear = component.IssueYear,
                ExpirationYear = component.ExpirationYear,
            };

            var noOpSave = CreateSaveMessage(component, actor);
            entMan.EventBus.RaiseLocalEvent(passport, noOpSave);

            Assert.That(component.Record, Is.Not.Null);
            Assert.That(component.Record!.FullName, Is.EqualTo("Test Person"));

            var changedSave = new PassportSaveMessage(
                component.Cover,
                "Changed Person",
                component.Age,
                component.Species,
                component.Sex,
                component.HeightCm,
                component.Nationality,
                "Changed Employer",
                "Changed Lifepath",
                "ZZZZZ-YYYYY-XXXXX",
                component.IssueYear,
                component.ExpirationYear)
            {
                Actor = actor,
            };
            entMan.EventBus.RaiseLocalEvent(passport, changedSave);

            Assert.Multiple(() =>
            {
                // What the document reads follows the forger.
                Assert.That(component.FullName, Is.EqualTo("Changed Person"));
                Assert.That(component.Employer, Is.EqualTo("Changed Employer"));
                Assert.That(component.Lifepath, Is.EqualTo("Changed Lifepath"));
                Assert.That(component.PassportId, Is.EqualTo("ZZZZZ-YYYYY-XXXXX"));

                // What the registry holds does not, which is what the printout exposes.
                Assert.That(component.Record!.FullName, Is.EqualTo("Test Person"));
                Assert.That(component.Record.Employer, Is.EqualTo("Test Employer"));
                Assert.That(component.Record.Lifepath, Is.EqualTo("Test Lifepath"));
                Assert.That(component.Record.PassportId, Is.EqualTo("ABCDE-FGHIJ-KLMNO"));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static PassportSaveMessage CreateSaveMessage(PassportComponent component, EntityUid actor)
    {
        return new PassportSaveMessage(
            component.Cover,
            component.FullName,
            component.Age,
            component.Species,
            component.Sex,
            component.HeightCm,
            component.Nationality,
            component.Employer,
            component.Lifepath,
            component.PassportId,
            component.IssueYear,
            component.ExpirationYear)
        {
            Actor = actor,
        };
    }
}
