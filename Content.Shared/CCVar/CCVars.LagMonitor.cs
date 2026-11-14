using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // lag monitor for admins (lagmonitor / lagreport)
    public static readonly CVarDef<bool> LagMonitorEnabled =
        CVarDef.Create("lagmonitor.enabled", true, CVar.SERVERONLY);

    // tick gap in ms that counts as a freeze
    public static readonly CVarDef<int> LagMonitorHitchMs =
        CVarDef.Create("lagmonitor.hitch_ms", 200, CVar.SERVERONLY);

    // freezes this long (ms) go to admin chat
    public static readonly CVarDef<int> LagMonitorAlertMs =
        CVarDef.Create("lagmonitor.alert_ms", 500, CVar.SERVERONLY);

    public static readonly CVarDef<bool> LagMonitorAlertAdmins =
        CVarDef.Create("lagmonitor.alert_admins", true, CVar.SERVERONLY);

    // seconds between lag alerts
    public static readonly CVarDef<int> LagMonitorAlertCooldown =
        CVarDef.Create("lagmonitor.alert_cooldown", 60, CVar.SERVERONLY);
}
