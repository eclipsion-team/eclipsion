using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Utility;

namespace Content.Shared._Crescent.CCVar
{
    [CVarDefs]
    public sealed class RatCCVars : CVars
    {
        /// <summary>
        /// Whether automatic debris cleanup is enabled.
        /// </summary>
        public static readonly CVarDef<bool> TrashCleanupEnabled =
            CVarDef.Create("trash.cleanup_enabled", false, CVar.SERVERONLY);

        /// <summary>
        /// Interval between debris cleanups, in seconds.
        /// </summary>
        public static readonly CVarDef<float> TrashCleanupInterval =
            CVarDef.Create("trash.cleanup_interval", 60f, CVar.SERVERONLY);

        /// <summary>
        /// Delay after round start before debris cleanup switches on, in seconds.
        /// </summary>
        public static readonly CVarDef<float> TrashCleanupStartDelay =
            CVarDef.Create("trash.cleanup_start_delay", 60f, CVar.SERVERONLY);

        /// <summary>
        /// Enables/disables automatic deletion of small grids.
        /// </summary>
        public static readonly CVarDef<bool> AutoGridCleanupEnabled =
            CVarDef.Create("shuttle.grid_cleanup_enabled", false, CVar.SERVERONLY | CVar.ARCHIVE);

        /// <summary>
        /// Whether population-scaled join caps are enforced. Off means the counts are still tracked and
        /// shown in the late-join window, but nobody is refused.
        /// </summary>
        public static readonly CVarDef<bool> FactionBalanceEnabled =
            CVarDef.Create("game.faction_balance_enabled", false, CVar.SERVERONLY | CVar.ARCHIVE);

        /// <summary>
        /// Headcount every balanced faction may always reach regardless of the population maths, so an
        /// empty or nearly empty server stays joinable.
        /// </summary>
        public static readonly CVarDef<int> FactionBalanceBaseSlots =
            CVarDef.Create("game.faction_balance_base_slots", 3, CVar.SERVERONLY | CVar.ARCHIVE);

        /// <summary>
        /// Extra headroom on top of each faction's computed share. 0 holds the parity factions to a lead
        /// of one player; raise it to loosen the grip.
        /// </summary>
        public static readonly CVarDef<int> FactionBalanceTolerance =
            CVarDef.Create("game.faction_balance_tolerance", 0, CVar.SERVERONLY | CVar.ARCHIVE);

        /// <summary>
        /// Whether admins ignore the join caps.
        /// </summary>
        public static readonly CVarDef<bool> FactionBalanceAdminBypass =
            CVarDef.Create("game.faction_balance_admin_bypass", true, CVar.SERVERONLY | CVar.ARCHIVE);

        /// <summary>
        /// Master switch for the chat word filter (see chatFilter prototypes).
        /// Deliberately not ARCHIVE: an admin can switch it off for the round, but it is always back on next restart.
        /// </summary>
        public static readonly CVarDef<bool> ChatFilterEnabled =
            CVarDef.Create("chat.filter_enabled", true, CVar.SERVERONLY);

        /// <summary>
        /// Whether Profanity-severity filters block messages. Slur filters are always applied while the filter is enabled.
        /// </summary>
        public static readonly CVarDef<bool> ChatFilterProfanity =
            CVarDef.Create("chat.filter_profanity", true, CVar.SERVERONLY);

        /// <summary>
        /// Whether online admins are alerted when a message is blocked for containing a slur.
        /// </summary>
        public static readonly CVarDef<bool> ChatFilterAdminAlert =
            CVarDef.Create("chat.filter_admin_alert", true, CVar.SERVERONLY);

        public static readonly CVarDef<bool> GridBlueprintEnabled =
            CVarDef.Create("blueprint.enabled", true, CVar.SERVERONLY | CVar.ARCHIVE);

        public static readonly CVarDef<float> GridBlueprintCooldown =
            CVarDef.Create("blueprint.cooldown", 120f, CVar.SERVERONLY | CVar.ARCHIVE);

        public static readonly CVarDef<int> GridBlueprintMaxSizeKb =
            CVarDef.Create("blueprint.max_size_kb", 4096, CVar.SERVERONLY | CVar.ARCHIVE);
    }
}
