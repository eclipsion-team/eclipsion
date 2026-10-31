using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Crescent.CCvars;
using Content.Shared._Crescent.ShipPower;
using Content.Shared._Crescent.Weapons.Ranged;
using Content.Shared.Examine;
using Content.Shared.Power.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Configuration;

namespace Content.Server._Crescent.ShipPower;

/// <summary>
/// Bills a ship weapon's battery for every shot, and trims a burst down to what it can actually afford.
/// </summary>
public sealed class WeaponPowerDrawSystem : EntitySystem
{
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly PowerNetSystem _powerNet = default!;
    [Dependency] private readonly PowerReceiverSystem _powerReceiver = default!;

    private bool _powerDrawEnabled;

    public override void Initialize()
    {
        base.Initialize();

        _powerDrawEnabled = _config.GetCVar(CrescentCVars.ShipSystemsPowerDrawEnabled);

        SubscribeLocalEvent<WeaponPowerDrawComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<WeaponPowerDrawComponent, AttemptShootEvent>(OnAttemptShoot);
        SubscribeLocalEvent<WeaponPowerDrawComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<WeaponPowerDrawComponent, ExaminedEvent>(OnExamined);
    }

    private void OnStartup(Entity<WeaponPowerDrawComponent> ent, ref ComponentStartup args)
    {
        if (_powerDrawEnabled)
            return;

        if (TryComp<ApcPowerReceiverComponent>(ent, out var receiver))
        {
            receiver.Load = 0f;
            _powerReceiver.SetNeedsPower(ent, false, receiver);
        }

        _powerNet.SetReceiverBatteryPowerDraw(ent.Owner, false);
    }

    private void OnAttemptShoot(Entity<WeaponPowerDrawComponent> ent, ref AttemptShootEvent args)
    {
        if (!_powerDrawEnabled || args.Cancelled || args.Shots <= 0 || ent.Comp.EnergyPerShot <= 0f)
            return;

        if (!TryComp<BatteryComponent>(ent, out var battery))
        {
            args.Cancelled = true;
            args.Message = Loc.GetString("ship-weapon-no-charge");
            return;
        }

        // A full-salvo weapon must be able to fund the entire burst before it starts. Otherwise the
        // per-tick shot limit below can let it begin a salvo and stop halfway through when the battery empties.
        if (TryComp<FullSalvoComponent>(ent, out var fullSalvo) &&
            TryComp<GunComponent>(ent, out var gun) &&
            !gun.BurstActivated &&
            battery.CurrentCharge < ent.Comp.EnergyPerShot * fullSalvo.RequiredShots)
        {
            args.Cancelled = true;
            args.Message = Loc.GetString("ship-weapon-no-charge");
            return;
        }

        var affordableShots = (int) Math.Min(
            args.Shots,
            MathF.Floor(battery.CurrentCharge / ent.Comp.EnergyPerShot));

        if (affordableShots > 0)
        {
            args.Shots = affordableShots;
            return;
        }

        args.Cancelled = true;
        args.Message = Loc.GetString("ship-weapon-no-charge");
    }

    private void OnGunShot(Entity<WeaponPowerDrawComponent> ent, ref GunShotEvent args)
    {
        if (!_powerDrawEnabled || ent.Comp.EnergyPerShot <= 0f)
            return;

        var shots = Math.Max(1, args.Ammo.Count);
        _battery.UseCharge(ent.Owner, ent.Comp.EnergyPerShot * shots);
    }

    private void OnExamined(Entity<WeaponPowerDrawComponent> ent, ref ExaminedEvent args)
    {
        if (!_powerDrawEnabled)
            return;

        if (!TryComp<BatteryComponent>(ent, out var battery))
            return;

        args.PushMarkup(Loc.GetString("ship-weapon-power-examine",
            ("shot", (int) ent.Comp.EnergyPerShot),
            ("charge", (int) battery.CurrentCharge),
            ("max", (int) battery.MaxCharge)));

        if (TryComp<ApcPowerReceiverBatteryComponent>(ent, out var apcBattery))
        {
            args.PushMarkup(Loc.GetString("ship-weapon-power-examine-draw",
                ("draw", (int) apcBattery.BatteryRechargeRate)));
        }

        if (battery.CurrentCharge < ent.Comp.EnergyPerShot)
            args.PushMarkup(Loc.GetString("ship-weapon-power-examine-flat"));
    }
}
