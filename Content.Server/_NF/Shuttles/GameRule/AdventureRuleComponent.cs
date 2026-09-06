using System.ComponentModel.DataAnnotations;
using Content.Server.GameTicking.Configuration;
using Robust.Shared.Audio;

namespace Content.Server.GameTicking.Rules.Components;

[RegisterComponent, Access(typeof(NfAdventureRuleSystem))]
public sealed partial class AdventureRuleComponent : Component
{
    [DataField("gameMapsByID", required: false)]
    public Dictionary<string, HullrotMapElementGameMapID> GameMapsID = new();

    /// <summary>
    /// The worldgen config prototype applied when this gamemode starts. Defaults to the standard
    /// RatWorld layout. Point this at a different worldgenConfig (e.g. RatWorldEmpty) to change
    /// the overworld asteroid layout for this mode only. Applied via the worldgen.worldgen_config
    /// CVar when the rule is added, before RoundStartingEvent runs the worldgen setup.
    /// </summary>
    [DataField]
    public string WorldgenConfig = "RatWorld";

}
