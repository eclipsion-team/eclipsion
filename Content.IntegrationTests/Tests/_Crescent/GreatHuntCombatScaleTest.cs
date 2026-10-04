using Content.Server.GameTicking;
using Content.Server._Crescent.GreatHunt;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Weapons.Melee;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Crescent;

/// <summary>
/// Great Hunt: Blades halves weapon damage and the stamina damage attackers deal, and leaves one's own stamina costs
/// and the blunt stamina that already follows the halved damage alone.
/// </summary>
[TestFixture]
[TestOf(typeof(GreatHuntCombatScaleRuleSystem))]
public sealed class GreatHuntCombatScaleTest
{
    [Test]
    public async Task BladesHalvesDamageAndStamina()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var ticker = entMan.System<GameTicker>();
            var melee = entMan.System<SharedMeleeWeaponSystem>();
            var thrown = entMan.System<SharedDamageOtherOnHitSystem>();
            var stamina = entMan.System<StaminaSystem>();

            var attacker = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var target = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var khopesh = entMan.SpawnEntity("DSMDuelingKhopesh", MapCoordinates.Nullspace);
            var hammer = entMan.SpawnEntity("SecBreachingHammer", MapCoordinates.Nullspace);

            var meleeBefore = melee.GetDamage(khopesh, attacker).GetTotal();
            var thrownBefore = thrown.GetDamage(hammer, user: attacker).GetTotal();
            Assert.That(meleeBefore.Float(), Is.GreaterThan(0f));
            Assert.That(thrownBefore.Float(), Is.GreaterThan(0f));

            Assert.That(ticker.StartGameRule("GreatHuntMeleeCombatScale"), Is.True);

            Assert.That(melee.GetDamage(khopesh, attacker).GetTotal().Float(),
                Is.EqualTo(meleeBefore.Float() / 2).Within(0.01f), "Melee damage is halved.");
            Assert.That(thrown.GetDamage(hammer, user: attacker).GetTotal().Float(),
                Is.EqualTo(thrownBefore.Float() / 2).Within(0.01f), "Thrown damage is halved.");

            var targetStamina = entMan.GetComponent<StaminaComponent>(target);
            stamina.TakeStaminaDamage(target, 20f, targetStamina, source: attacker);
            Assert.That(targetStamina.StaminaDamage, Is.EqualTo(10f).Within(0.01f),
                "Stamina dealt by an attacker is halved.");

            stamina.TakeStaminaDamage(target, 20f, targetStamina, source: attacker, fromDamage: true);
            Assert.That(targetStamina.StaminaDamage, Is.EqualTo(30f).Within(0.01f),
                "Stamina worked out from already halved damage is not halved again.");

            var attackerStamina = entMan.GetComponent<StaminaComponent>(attacker);
            stamina.TakeStaminaDamage(attacker, 20f, attackerStamina);
            Assert.That(attackerStamina.StaminaDamage, Is.EqualTo(20f).Within(0.01f),
                "One's own stamina costs are untouched.");

            entMan.DeleteEntity(attacker);
            entMan.DeleteEntity(target);
            entMan.DeleteEntity(khopesh);
            entMan.DeleteEntity(hammer);
        });

        await pair.CleanReturnAsync();
    }
}
