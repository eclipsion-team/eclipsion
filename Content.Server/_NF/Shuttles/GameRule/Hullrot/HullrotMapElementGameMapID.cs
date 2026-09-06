using System.ComponentModel.DataAnnotations;
using System.Numerics;
using Content.Server.Maps.NameGenerators;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Server.GameTicking.Configuration;

/// <summary>
/// A config for a hullrot map element, defined by a gameMap prototype's ID. These are usually stations like Vladzena.
/// </summary>
[DataDefinition, PublicAPI]
public sealed partial class HullrotMapElementGameMapID
{
    /// <summary>
    /// This string matches the specific gameMap prototype's ID field. This tells the game what thing to actually spawn, and gets the path from that prototype too.
    /// </summary>
    [DataField("gameMapID", required: true)]
    public string GameMapID = "";

    [DataField("posX", required: true)]
    public float PositionX = 0f;

    [DataField("posY", required: true)]
    public float PositionY = 0f;

    [DataField("IFFColor", required: false)]
    public Color IFFColor = Color.White;

    [DataField("HideIFF", required: false)]
    public bool HideIFF = false;

    /// <summary>
    /// This float decides the maximum random offset for X for this map element when it spawns. Leave unconfigured or at 0 if you want it fixed.
    /// </summary>
    [DataField("randomOffsetX", required: false)]
    public float RandomOffsetX = 0f;

    /// <summary>
    /// This float decides the maximum random offset for y for this map element when it spawns. Leave unconfigured or at 0 if you want it fixed.
    /// </summary>
    [DataField("randomOffsetY", required: false)]
    public float RandomOffsetY = 0f;

    /// <summary>
    /// Pins the element where it spawns: static body, fixed rotation, nothing can shove it off station.
    /// Every grid is handed a ShuttleComponent on init (ShuttleSystem.OnGridInit), which leaves belts, wrecks
    /// and derelicts floating free by default, so set this on anything nobody is ever meant to fly.
    /// </summary>
    [DataField("pinned", required: false)]
    public bool Pinned = false;

    /// <summary>
    /// Inner radius of the ring this element spawns on, measured from posX/posY. Only read when randomRingMax is set.
    /// Leave at 0 for a full disc, so the element can land anywhere inside randomRingMax rather than only in an
    /// outer band - a band is its own predictable loot route, just a bigger one.
    /// </summary>
    [DataField("randomRingMin", required: false)]
    public float RandomRingMin = 0f;

    /// <summary>
    /// Outer radius of the ring this element spawns on. Leave at 0 to use posX/posY plus the randomOffset box instead.
    /// A ring rolls a fresh angle every round, so the element does not come back in the same corner of the sector.
    /// </summary>
    [DataField("randomRingMax", required: false)]
    public float RandomRingMax = 0f;

    /// <summary>
    /// How far a ring roll has to stay from everything already placed this round. 0 accepts the first roll.
    /// </summary>
    [DataField("minClearance", required: false)]
    public float MinClearance = 0f;

    /// <summary>
    /// This string sets the IFF for this particular object. Leave "null" to not modify IFF.
    /// </summary>
    [DataField("IFFFaction", required: false)]
    public string? IFFFaction = null;

    /// <summary>
    /// Overrides the name the grid carries on radar, replacing the one the gameMap's StationNameSetup gave it.
    /// Leave null to keep that name.
    /// </summary>
    /// <remarks>
    /// Only the grid is renamed; the station entity keeps its real name, so the boarding announcement, admin
    /// tools and round end still say which wreck it is. Give several elements the same label and they are
    /// indistinguishable at range - a contact that reads "Derelict" tells a pilot something is out there
    /// without telling them which hull it is or whether anyone has already stripped it.
    /// </remarks>
    [DataField("IFFLabel", required: false)]
    public string? IFFLabel = null;

}
