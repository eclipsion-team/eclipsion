# Persistent capture regions

A persistent capture region turns any mapped installation into ground the four major powers can take from each
other, and keeps the result across round restarts and server restarts. Take a mining outpost as DSM and it becomes
"DSM Fenwick Extraction" on radar in DSM colours, its point-defence console answers only to DSM crew, and its
anti-boarder turrets shoot everyone DSM is hostile to. Lose it and all of that goes with it.

Ownership lives in `capture_regions.json` in the server's user data, keyed by region ID, under the folder of the
gamemode's territory campaign (see **Campaigns** below). The map file is never rewritten.

## Campaigns

Every gamemode plays its own territory campaign. What DSM took in Freeplay is not DSM's in DSM vs NCWL, and the other
way round: holders, holder-given names, levels, production lines and persistent grids are all kept per campaign under
`territories/<campaign>/` in the server's user data (`capture_regions.json`, `territory_progress.json`,
`persistent_pois/`). A campaign is set by `territoryProfile` on the gamemode's `AdventureRule`; two rules with the same
profile share one, and a rule without one uses its own ID.

| Campaign    | Gamemodes                                | Who can take ground                      |
|-------------|------------------------------------------|------------------------------------------|
| `Freeplay`  | Freeplay (`Adventure`)                   | DSM, NCWL, TFCF, SHI                     |
| `DSMvsNCWL` | Freeplay DSM & NCWL, Freeplay Balreska   | DSM, NCWL                                |
| `TFCFvsSHI` | Freeplay TFCF & SHI                      | TFCF, SHI                                |
| `TAPvsTFCF` | Freeplay TAP, Gliess & TFCF              | TFCF (TAP cannot hold territory)         |
| `Hyperwar`  | Hyperwar                                 | DSM, NCWL                                |

Who can take ground is every faction whose home station the gamemode spawns, unless the rule lists
`territoryFactions` itself. Anyone else working a standard is told their faction is not contesting territory in this
sector, and admins cannot hand them ground either. The campaign is picked when the gamemode's rule is added, before
anything spawns, and stays on the last round's between rounds, so commands in the lobby act on the campaign that was
just played. Files from before campaigns existed (`capture_regions.json` and the rest in the user data root) are no
longer read.

## Mapping one

Everything below is in the mapping template list under Crescent hardpoints and consoles.

1. **Put the grid together first and name it.** The grid's name is the territory's name with nobody holding it.
   Ownership is displayed by prefixing it, so name it `Fenwick Extraction`, not `DSM Fenwick Extraction`. A grid left
   with the map editor's default name, `grid`, goes by its `regionId` instead; setting `baseName` on the flag
   overrides both.

2. **Place one `PersistentCaptureRegionFlag`** on that grid, somewhere a boarding party can reach and manually
   operate without moving or taking damage. Give it a `regionId` that is unique across every map on the server and
   that you will never change:

   ```yaml
   - type: PersistentCaptureRegion
     regionId: fenwick-extraction
   ```

   The region ID is the save key. Renaming it orphans the saved owner and the territory silently reverts to
   whatever the map says. A flag left on the `REPLACE_WITH_UNIQUE_REGION_ID` placeholder, a duplicate ID, a flag
   not on a grid, or a second flag on a grid that already has one is rejected at map load with an error in the
   server log, and that flag does nothing at all for the rest of the round.

3. **Place the defences.** Both of these take their faction from the flag, on every map load and on every capture,
   so never map the faction-specific versions inside a capturable region.

   - `WeaponTurretAutoPDCapturable` for anti-boarder turrets. Unowned, it has no factions and shoots nobody.
   - `ComputerTargetingCapturable` for the point-defence console, plus ordinary hardpoints and ship guns for it to
     control. Unowned, the console is locked to everyone.

4. **Optionally give it a starting owner** by setting `ownerTeam` on the flag's `CaptureFlag` component. That
   applies at map load but is not written to the save, so the map stays the source of truth until a player takes
   the region for the first time.

A territory that spans several grids sets `regionId` explicitly on the devices on the other grids:

```yaml
- type: CaptureRegionDevice
  regionId: fenwick-extraction
```

Only the flag's own grid is renamed and recoloured on radar. Devices bound across grids still change hands.

## Who can take it

DSM, NCWL, TFSC (the TFCF's ID) and SHI, and nobody else. The restriction is not cosmetic: each of those four has
an `npcFaction` prototype for the turrets to join and a banner sprite state for the flag to fly. ATH, TAP, TSP,
SRM and IND characters are ignored by the flag entirely - they neither capture nor contest, and they cannot use a
captured console. Adding a fifth power means adding its `npcFaction` prototype, a banner state in
`medieval_pole_banners.rsi`, an entry in `PersistentTerritoryFactions`, and radar and flag colours. For its
batteries to know who to shoot it also needs a `diplomacy` prototype and a place on `RatDiplomacySystem`'s
roster, and for the territory to move its stock price, a row in `FactionStockCompanies`.

Capture needs a living body and an explicit interaction with the standard; proximity alone does nothing. Corpses,
crit characters and unsupported factions cannot start an attempt. Moving away or taking damage interrupts the
do-after and discards all progress, and only one person can work a standard at a time. Since nothing happens by
standing near it, a territory's standard draws no capture circle round itself, as the other capture flags do; the
do-after bar shows the progress.

Taking held ground remains two manual stages at the times on the flag's `CaptureFlag` component: interact once and
hold still to neutralise it, then interact again and hold still to raise your own colours. Neither stage, nor giving
ground up, nor raising a level is announced to the sector; the political map and the radar contact show who holds what.

The moment somebody starts hauling a holder's colours down, every member of the holding faction who is online gets a
"Sector Registry" alert with the attacker and the territory's name - at most once a minute per territory, so starting
and abandoning the standard over and over cannot flood anyone's chat. Their mainframe's Territories tab shows the
attack and how far along it is.

## The point-defence console

`ComputerTargetingCapturable` is an ordinary targeting console with two additions.

**Access.** Its `FactionMachine` is restricted, so only members of the holding faction can open it or send it a
message. An unowned territory's console is locked to everybody rather than open to everybody, and a capture closes
the window on anyone from the previous owner still standing at it.

**Automatic guard mode** (`TerritoryAutoDefense`). With nobody at the console it acquires and fires on its own,
and stands down the instant an operator opens the UI. Two consoles on one grid never both run - the lower entity
ID takes it. Defaults on the prototype:

| Field                   | Default | Meaning                                                      |
|-------------------------|---------|--------------------------------------------------------------|
| `range`                 | 512     | Acquisition range, matched to the console's own radar range   |
| `neutralEngagementRange`| 384     | Exclusion zone, only used when `warOnly` is off               |
| `scanInterval`          | 1       | Seconds between target searches and cannon relinks            |
| `warOnly`               | true    | Engage only declared wars, and ignore everything else         |

With `warOnly` on, the battery shoots hulls whose faction is at war with the holder and ignores the rest silently.
Turning it off makes the territory hostile ground for all non-allies: anything crossing `neutralEngagementRange`
is fired on, and traffic between there and `range` gets a radio warning naming the clearance it must keep.

The battery holds fire when a grid that is not at war with the holder is in the line of shot, and only fires guns
that can actually bear - a welded mount has to already be pointing at the target, a turret swings onto it.

## Admin console

```
territory list                          every region in the save or on the current map, with level and line
territory set <regionId> <faction>       hand a region to one of the campaign's factions
territory clear <regionId>               return a region to nobody
territory forget <regionId>              drop the save row, so the map's own owner applies next load
territory level <regionId> <level>       set a region's development level
territory capability <regionId> <line>   retool a region's production (GeneralOre, TargetedOre, Textiles, Materials, TradeGoods, none)
territory name <regionId> [name]         name a held region, or give it back its own name
territory profile                        this round's campaign, its factions, and every campaign on disk
territory reset <regionId>               start one region over: no holder or name, level 1, no line, grid from the map
territory resetfaction <faction>         take every region a faction holds off it; levels stay with the ground
territory resetall <campaign>            start a whole campaign over - type its name to confirm
```

Everything acts on this round's campaign except `resetall`, which takes any campaign by name. `set` and `clear` work
whether or not the region's map is loaded, and take effect immediately when it is. `reset` and `resetall` set persistent grids back to their map files the way `persistentpoi reset` does:
a grid loaded right now plays on unchanged, but its round is not saved over the result. `resetall` moves the
campaign's folder aside as `territories/<campaign>.reset-<time>` rather than deleting it, so a mistake can be undone by
moving it back while the server is down.

## Permanent ground

The grid a valid standard stands on is permanent. It is anchored where it lies - a static body that nothing can shove,
no longer a shuttle, impossible to pilot - and nothing can set it adrift again: `ShuttleSystem.Enable` refuses it, so
not even a station anchor losing power frees it.

**The permanent floor.** No floor tile within `protectedCoreRadius` (15 tiles by default, set on the flag's
`PersistentCaptureRegion`) of the standard can be removed: explosions, ship-gun hits, ramming, RCDs, crowbars, shovels,
ship drills, gravity anomalies and psionics, singularities and immovable rods all leave it alone, so the territory can
never be wiped out. That floor is all that is protected. Everything
standing on it - walls, windows, doors, machines, consoles, guns - can be destroyed like anywhere else, and the floor
past the radius breaks like any other. The grid never splits: blown-out floor leaves holes, never a piece drifting off
on its own (which would otherwise carry the standard away whenever its piece was the smaller one).

## Building on held ground

A member of the holding faction who stands on the territory, conscious, gets a **Build territory works** action. It
opens the **Territory Works** window: pages down the left, the page's works in the middle, the picked one in full on
the right. Every works shows what it costs, how many of its kind the territory has against what its level allows, and,
greyed out, why it cannot go up right now - not tooled for that line, needs level 2, full until the next level. Other
factions' own works (their shipyards) are never listed. A search box looks across every page. **Place** starts ordinary
construction placement and closes the window; the recipes only place on a territory grid the builder's faction holds,
with the builder on it, and only while the level has room for another of their kind. The action goes away the moment
they step off, go down, or their faction loses the ground.

**Works go up in stages, one material each.** The ghost is raised with the first one - a frame, an airlock assembly, a
girder - and every further material is used on that frame by hand: an administration console is 10 steel for the
frame, then 5 glass, then 10 cable. Examining a frame says what goes in next. Each stage takes its whole amount off a
single stack, from the builder's hands, worn bags and pockets or the floor beside them, and no stage asks for more
than one stack holds (30), so a big job like a shipyard is split over several stages. The window lists the stages and
how much of each material the builder has on them - amber when it is all there but only in stacks too small to use.
Frames cannot be taken apart, only shot down.

| Page             | Recipe                                | Builds                                             | Stages                                     |
|------------------|---------------------------------------|----------------------------------------------------|--------------------------------------------|
| Command          | territorial administration console    | `ComputerPersistentPoiAdministration`              | 10 steel, 5 glass, 10 cable                |
| Command          | territorial production console        | `ComputerPersistentPoiProduction`                  | 10 steel, 5 glass, 10 cable                |
| Command          | territorial radar console             | `ComputerRadarPersistentPoi` (holder only)         | 10 steel, 5 glass, 10 cable                |
| Command          | territorial shuttle console           | `ComputerShuttlePersistentPoi` (holder only)       | 10 steel, 5 glass, 10 cable                |
| Defences         | territorial point-defence console     | `ComputerTargetingPersistentPoi`                   | 10 steel, 5 glass, 10 cable                |
| Defences         | territorial point-defence emplacement | `HardpointPersistentPoiLaserEmplacement` + its 150cc 'Retribution' laser (needs power) | 10 plasteel, 15 steel, 10 cable |
| Defences         | territorial anti-boarder turret       | `WeaponTurretAutoPDPersistentPoi`                  | 15 steel, 5 cable                          |
| Power & light    | territorial boriatic generator        | `BoriaticGeneratorPersistentPoi` (50 kW, empty tank) | 15 steel, 10 plastic, 5 gold, 10 cable   |
| Power & light    | territorial SMES                      | `SMESPersistentPoi` (built empty)                  | 10 steel, 5 plasteel, 10 cable             |
| Power & light    | territorial substation                | `SubstationPersistentPoi`                          | 10 steel, 10 cable                         |
| Power & light    | territorial APC (on a wall)           | `APCPersistentPoi`                                 | 3 steel, 5 cable                           |
| Power & light    | territorial wall light                | `PoweredlightPersistentPoi`                        | 2 steel                                    |
| Power & light    | territorial small light               | `PoweredSmallLightPersistentPoi`                   | 1 steel                                    |
| Atmospherics     | territorial air canister              | `AirCanisterPersistentPoi` (full of air, holder only) | 15 steel                                |
| Atmospherics     | territorial connector port            | `GasPortPersistentPoi`                             | 2 steel                                    |
| Atmospherics     | territorial air vent / air scrubber   | `GasVentPumpPersistentPoi` / `GasVentScrubberPersistentPoi` | 2 steel                           |
| Atmospherics     | territorial passive vent              | `GasPassiveVentPersistentPoi`                      | 2 steel                                    |
| Atmospherics     | territorial gas pipe (straight, bend, T, four-way) | `GasPipe<Shape>PersistentPoi`         | 1 steel                                    |
| Atmospherics     | territorial air alarm (on a wall)     | `AirAlarmPersistentPoi` (holder only)              | 2 steel, 5 cable                           |
| Doors & airlocks | territorial airlock                   | `AirlockPersistentPoi` (holder only)               | 4 steel, 5 cable                           |
| Doors & airlocks | territorial glass airlock             | `AirlockGlassPersistentPoi` (holder only)          | 4 steel, 2 reinforced glass, 5 cable       |
| Doors & airlocks | public airlock                        | `AirlockPublicPersistentPoi` (anyone)              | 4 steel, 5 cable                           |
| Doors & airlocks | territorial external airlock          | `AirlockPersistentPoiDock` (docking, holder only)  | 10 steel, 5 plasteel, 5 cable              |
| Walls & windows  | territorial wall                      | `WallSolidPersistentPoi`                           | 5 steel                                    |
| Walls & windows  | territorial reinforced wall           | `WallReinforcedPersistentPoi`                      | 4 steel (girder), 3 plasteel               |
| Walls & windows  | territorial reinforced window         | `ReinforcedWindowPersistentPoi`                    | 4 reinforced glass                         |
| Walls & windows  | territorial window                    | `WindowPersistentPoi`                              | 4 glass                                    |
| Furniture        | chair, comfy chair, office chairs, bar stool, steel bench | `ChairPersistentPoi`, `ComfyChairPersistentPoi`, `ChairOffice<Light/Dark>PersistentPoi`, `StoolBarPersistentPoi`, `SteelBenchPersistentPoi` | 1-2 steel |
| Furniture        | table, reinforced table, counter      | `TablePersistentPoi`, `TableReinforcedPersistentPoi`, `TableCounterMetalPersistentPoi` | 2 steel / 2 plasteel / 3 steel |
| Furniture        | bed, medical bed                      | `BedPersistentPoi`, `MedicalBedPersistentPoi`      | 2 steel, 2 cloth / 2 plasteel, 2 durathread |
| Furniture        | closet, rack                          | `ClosetPersistentPoi`, `RackPersistentPoi`         | 4 steel / 2 steel                          |
| Furniture        | sink, shower, toilet                  | `SinkPersistentPoi`, `ShowerPersistentPoi`, `ToiletPersistentPoi` | 3 / 5 / 5 steel           |
| Production       | one works per line of production      | see below; other lines are greyed out              | 20-30 steel, then plasteel/cloth/glass, cable |
| Shipyard         | territorial (faction) shipyard        | `ComputerPersistentPoiShipyard<DSM/NCWL/TFSC/SHI>` | 20 steel, 20 steel, 20 plasteel, 10 glass, 20 cable |
| Shipyard         | territorial external airlock          | the same recipe as under Doors & airlocks          |                                            |
| Deployment       | territorial cryo spawner              | `PersistentPoiCryoSpawner`                         | 20 steel, 20 plasteel, 10 glass, 20 cable  |
| Deployment       | territorial (faction) equipment rack  | `PersistentPoiGearRack<DSM/NCWL/TFSC/SHI>`         | 20 steel, 10 plasteel, 10 cable            |

**Floor markings** has a button of its own under the pages; see below.

**Power.** Consoles, turrets and lights run off an APC; the production works draw from medium-voltage cable laid under
them. The menu's generator is the boriatic 'Hercules', bolted down (`BoriaticGeneratorPersistentPoi`): rated for 50 kW
onto high-voltage cable (a multitool switches it to medium), built with an **empty tank**, so the holder fuels it with
boriatic fuel and starts it. Fuel left in it is kept with the territory between rounds, to the unit, as is an AME
built there and the fuel jar in its controller (`PersistentPoiTest.TerritorySurvivesSaveAndLoad` checks both). Fuel
left lying about - a jug on the floor, a jar in a closet - is cleared away with the round's other loose belongings, so
bunker it in the machines. A wrecked generator goes
up in proportion to what is in its tank. A substation steps high voltage down to medium, and APCs feed off that; an
SMES between the generators and the substation (built empty) banks power for when the fuel runs out. The level caps
the generators (2 / 4 / 6). Cable is laid by hand, as anywhere. The menu used to build fuel-free RTGs
(`GeneratorRTGPersistentPoi`) and no longer offers them, but RTGs already standing in a save - and RTG frames left half
done - keep working and count against the same cap.

**Air.** A territorial air canister comes full of air. Build a connector port, the canister on top of it, and pipe from
the port to air vents; scrubbers pipe their waste to a passive vent outside (or into a canister). Vents and scrubbers
work unattended at normal pressure; an air alarm linked to them with a multitool sets them otherwise. Pipes can run
under walls. Anyone can look at the canister and the air alarm, but only the holder's people can work them
(`FactionMachine`, stamped by `CaptureRegionDevice` like the consoles).

**Furniture.** Chairs (office chairs included), tables, counters, beds, closets, racks, sinks, showers and toilets.
Unlike everything else on the menu it is decoration, not equipment (`PersistentPoiFurnishing` instead of
`PersistentPoiStructure`): it goes up bolted down, but a wrench frees it, and anyone can move it about - an office chair
rolls once its wheels are unlocked. The territory keeps it between rounds wherever it stands on the grid, bolted or not.
Whatever is left in a closet, on a rack or in a toilet's cistern when the round ends is cleared away like any other
loose belongings.

**Doors.** Territorial airlocks, glass airlocks and external airlocks open only for the people of the faction holding
the territory (`TerritoryAccess`). Whose that is comes off their `FactionMachine`, which the territory stamps again
every time the ground changes hands (`CaptureRegionDevice`), so the doors change sides with it - and on unclaimed
ground they open for anybody. Membership is read off the body, like the consoles do, not off an ID card. Forcing one
works as on any door: cut its power and pry it, or shoot it down. Public airlocks open for everyone.

**Shipyard and dockyard.** Each of the four factions has its own territorial shipyard: its own fleet's listing (the
same one as its home yard), usable only by its own people (`FactionMachine` with `restrictAccess`), and the bought
ship docks on the territory's external airlocks - the same docking airlock every ship uses (`AirlockShuttle`). The
menu shows a holder only their own faction's yard (`PersistentPoiFactionRequired`). A yard stays its faction's after
the ground changes hands, so the next holder has to wreck it to build theirs. External airlocks go on the territory's
edge, facing out.

**Who pays.** Every faction yard - these and the home ones (`usesFactionTreasury: true`) - has a *Pay from* choice at
the top: the **faction treasury** or the buyer's **own account**. A treasury purchase keeps every treasury rule: the
per-player ship limit (`shipyard.faction_ship_limit`) and sign-off at a treasury console for one big enough. A personal
purchase has none of them. Sold back anywhere, a hull pays into whatever paid for it: treasury hulls into the treasury,
personal ones to the seller. A yard stamped with its own faction (`FactionMachine`, as the territorial yards are) bills
that faction's treasury and issues hulls in its colours, whoever holds the ground it stands on. Civilian yards always
bill the buyer.

**Deployment.** A level 3 territory can take a **cryo spawner** (one) and up to two **equipment racks**. While the
spawner is anchored, powered off an APC (5 kW) and its ground is held at level 3, the late-join window offers the
holder's people a *Deploy at* choice next to the job list: their home station, or the territory by name. They still
sign on with a home-station job and get its records and gear; only where they wake up changes. The choice is
checked again as they spawn, against the faction read off the new body, and anyone the spawner can no longer take
wakes up at home with a chat line saying so. The server pushes the list of live spawners
(`TerritoryDeployPointsEvent`) every 2 seconds when it changes, and `joingame <job> <station> [spawner]` carries the
pick (`TerritorySpawnBeaconSystem`). Examining a spawner says whether it is online. The equipment racks are each
faction's home armoury rack (class kit, armour, primary weapon, utilities, ammunition, drawn once per life), open to
any member of that faction - no armoury access needed - but only while that faction holds the ground
(`TerritoryHolderOnly` on top of `FactionMachine`). Like the shipyards, the menu shows a holder only their own rack.

Built equipment cannot be unanchored or deconstructed, but it can be shot to pieces, which is how the next holder gets
to build their own. An emplacement is one piece: its hardpoint and gun go down together. Emplacements built before
the switch to the laser keep their machinegun (`HardpointPersistentPoiEmplacement`, `WeaponTurretPDTPersistentPoi`);
those prototypes stay only so old saves load. The radar and shuttle consoles are one each per territory and only
look - the ground is fixed in place, so the shuttle console never flies or jumps it. The menu lives on the
`ActionPersistentPoiBuild` prototype (`persistent_poi.yml`) and the recipes in `persistent_poi_construction.yml`, so
adding something to it is YAML only: give the entity `PersistentPoiStructure` (and `PersistentPoiWorks` if it should
count against a limit); give the `PersistentPoiStructures` graph a node for it, reached in stages of one material
each, every stage after the first through a frame entity of its own (one frame prototype per recipe, sitting on its
node); end the last edge in a `PersistentPoiEnforceLimit` action if it is limited; add a hidden construction recipe
with the `PersistentPoiHeldGround` (and `PersistentPoiWithinLimit`) conditions, and list it on a page of the menu.
`PersistentPoiTest.BuildMenuOffersOnlyTerritoryWorks` checks all of that.

Limits are checked as building starts, so two builders racing for the last slot could both start one. The
`PersistentPoiEnforceLimit` action checks again as the last stage goes in: a frame that would put the territory past
its limit is taken down on the spot instead of becoming the works, and every stage's materials are dropped where it
stood.

## Development levels

Every territory has a development level, starting at 1. The holder raises it at the **administration console** by
using material stacks on it - or a bag or box of them, which hands over everything inside the level wants in one go;
it takes only what the next level still needs - and pressing *Raise to level N* once everything is in.
Levels are meant to be a long haul. Levels are defined in `persistent_poi_progression.yml`; out of
the box:

| Level | Costs to reach                                                                                          | Fire control | Factories | Anti-boarder turrets | Production | Bank | Stock reward |
|-------|---------------------------------------------------------------------------------------------------------|--------------|-----------|----------------------|------------|------|--------------|
| 1     | -                                                                                                       | 4 guns       | 2         | 2                    | 1x         | +0   | 1x           |
| 2     | 1500 steel, 600 glass, 600 plasteel, 150 gold, 150 silver, 2 advanced + 2 precursor electronic parts      | 6 guns       | 3         | 3                    | 1.5x       | +3   | 1.5x         |
| 3     | 3000 steel, 1200 glass, 1500 plasteel, 400 gold, 400 silver, 300 uranium, 5 advanced + 5 precursor parts | 8 guns       | 4         | 4                    | 2x         | +6   | 2x           |

Precursor electronic parts (`NanotrasenElectronics`) are salvage only - nothing makes them any more - which is what
makes the top level a real prize. Anti-boarder turrets never go past four.

Every level allows one console of each kind - fire control, production, administration - counting mapped ones.
A territorial shipyard needs level 2 (one at levels 2 and 3), external airlocks go 2 / 4 / 6, and so do territorial
generators. The cryo spawner (one) and equipment racks (two) need level 3.

**Fire control is capped by level.** Every anchored ship gun on the territory counts, whoever put it there. Past the
cap, the newest guns are left out of fire control entirely: no console links them and they will not fire, whatever
console asks. The fire-control console's status strip warns about it. Raising the level brings them back.

The level and the production capability are kept per region ID in the campaign's `territory_progress.json`, like
ownership, so they survive between rounds whether or not the grid itself is persistent, and pass to whoever
takes the ground. `levelsLostOnNeutralize` on the flag (0 by default) knocks levels off a territory whose holder is
driven out.

## Production

The production console tools the territory for one **line of production**:

| Line                    | Works                                 | Makes                                                   |
|-------------------------|---------------------------------------|---------------------------------------------------------|
| General ore extraction  | `PersistentPoiMixedOreRig`            | a mix of steel, copper, coal, plasma, silver, gold ore  |
| Targeted ore extraction | `PersistentPoiExtractionRig`          | one chosen ore per rig, in quantity                     |
| Textiles                | `PersistentPoiTextileMill`            | cotton, cloth, durathread                               |
| Materials               | `PersistentPoiFoundry`                | steel, glass, plastic, plasteel sheets                  |
| Trade goods             | `PersistentPoiTradeWorks`             | crated alloys, gunparts, ammunition, plasma             |

Only the current line's works can be built, up to the level's factory limit, and only they run. Retooling idles the
old line's works; the console can **decommission** works to free their place (anything they banked is dispensed first).

Works run while they are switched on, their territory has a holder, and a powered production console stands on the
same grid. They take their power straight off **medium-voltage cable** laid under them, the way the seep drill does -
20-35 kW each (`powerDraw`), more than an APC's 35 kW could hand out without blacking out everything else it feeds.
They draw only while they work, so switched-off, full or idled works leave the power to the rest of the territory.
Short of their full draw they run proportionally slower (the console shows the percentage), and without any they
stop, so a territory that cannot power its works does not produce. One substation's 150 kW runs four to seven works,
depending on kind, and little else. They bank finished batches instead of spitting them onto the floor, up to their `maxStored`
(plus the level's bonus), and the console dispenses them. The bank, progress and recipe are saved with the territory,
so whatever a holder leaves undispensed is waiting for whoever takes the ground next. Switching a works' recipe hands
out what it has banked first.

**Paid orders.** The production console also takes orders that cost credits, each paid from wherever the player
picks at the top of the console: their **own bank account**, or their **faction's treasury**.

- **Rush** (per works) finishes the current batch on the spot and banks it. It costs what the rest of the batch would
  sell for, times `rushMarkup` (1.5), at least `minimumRushPrice` (250). A works with no power cannot be rushed.
- **Overtime** (whole territory) runs every works `overtimeSpeed` (x2) faster for `overtimeDuration` (10 min); buying
  more stacks it, up to `maxOvertime` (30 min). It costs what the extra output would sell for, times
  `overtimeMarkup` (1.2), at least `minimumOvertimePrice` (500). Only works that would actually run count - full,
  idle or unpowered works make it no dearer and gain nothing from it. Overtime is not saved between rounds.

Prices come from what the output fetches on the market, so paying never turns credits into more than they are worth -
it only buys time. Spending from the treasury needs membership of the holding faction and is held to `treasuryShare`
(25%) of the treasury per member per round, counted in the same ledger as withdrawals at the treasury console, so
the two cannot be used to get round each other. Every payment goes to the admin log. The console is single-user
(it shows its user's balances), and both orders also work from the mainframe's territory tab, through the production
link like any other production order. All the numbers are fields on `PersistentPoiProductionConsole`.

The production and administration consoles are locked to the holding faction, like the point-defence console.
Recipes are `persistentPoiProductionRecipe` prototypes in `persistent_poi_production.yml`: `duration` is seconds of
running per batch at level 1, `outputs` is what one batch hands out.

## Running territories from home

Every faction's command mainframe has a **Territories** tab (`TerritoryConsole`, its `faction` set per mainframe
variant). It lists every territory the sector knows about, the faction's own first, with its level and holder - and
for the faction's own, whether the standard is under attack, how many works are running and how many batches are
waiting. Selecting one the faction holds shows the same development and production panels as the territory's own
consoles, and orders given there are carried out on the territory:

- **Production orders** - switching works on and off, changing recipes, retooling - go through the territory's
  production console. With no powered production console there, they do not get through.
- **Levels** are only shown here - what the next one costs and how much is paid in. Depositing materials and raising
  the level happen at the territory's own administration console, never from afar.
- Dispensing and decommissioning need somebody at the works, so the tab leaves them out. A works with batches waiting
  keeps its recipe until they are collected, so switching from afar never turns them into something else.

Every order from the tab has to come from one of the mainframe's own faction, read off the body: the mainframe's lock
reads an ID card, and a card can be stolen. Production orders are open to every member, as at the territory's own
production console.

The **Command** tab gives the territory's standing orders and runs without any console on the territory. The
mainframe opens for every member, so these orders also need the faction's command access, `commandAccess` on
`TerritoryConsole` (any one set will do, as on an access reader; empty lets every member give them). Each mainframe
variant uses the same command tier as its recruitment console - DSM: noble, adjutant or governor; NCWL: command or
kommissar; SHI: officer or executive; TFCF: command or ringleader. Everyone else sees the tab greyed out.

- **Defence.** *Automatic fire control* off makes the fire-control battery hold fire unless somebody mans it. *Hold
  exclusion zone* makes it engage every non-ally that closes in (the console's `neutralEngagementRange`), not only
  factions at war. *Anti-boarder turrets armed* off stands the turrets down. The tab also shows how many fire-control
  consoles have power, whether one is manned, what the battery is engaging, and the guns against the level's cap.
  The orders are saved with the grid and go back to the defaults (on, off, on) whenever the territory changes hands.
  **Take fire control** opens the territory's own targeting console on the commander's screen - radar, gun groups,
  firing, all of it - as if they sat at it. It needs a powered fire-control console of theirs on the territory, and
  it is taken back the moment they leave the mainframe's territory tab, the ground changes hands or the console loses
  power. While anybody mans it, the automatic battery holds off. The mapped and built fire-control consoles have no
  engine UI range for this (`interactionRange: 0`); `TerritoryRemoteFireSystem` holds people at the console itself
  to the usual two tiles instead.
- **Name.** The holder can name the territory. Its name then always reads as their abbreviation, a dash and the name:
  `DSM-Shipyard Station`, on radar, the political map, this tab and the under-attack alert. Up to 24 letters, digits,
  spaces and `- _ ' .`; the chat filter applies; once every 30 seconds. A new holder starts from the territory's own
  name. Admins: `territory name <regionId> [name]`.
- **Give it up.** Two presses strike the colours: the ground is left unclaimed and the territory
  loses levels as if it had been taken (`levelsLostOnNeutralize`). This also works for held ground out of the sector.

Other factions' territories show only who holds them and their level. Factions that cannot hold territory (TAP, SRM,
CMM) get the list and nothing else.

## Floor markings

**Paint floor markings** under the build menu's pages opens the decal painter. While its window is open, left click
paints the picked marking onto the tile under the mouse and right click scrubs every decal off it; holding either drags
along. Markings are whole-tile, in the holder's own colours, and turn in quarter steps. The colours are
`territoryDecalPalette` prototypes, by faction: three shades of their colour, the station departments' colour they go
with (alpha included, as mapped stations use it) and three neutrals:

| Faction | Departmental colour                                     |
|---------|---------------------------------------------------------|
| DSM     | epistemics `#D381C996`                                  |
| NCWL    | engineering `#EFB34196`                                 |
| TFCF    | security `#DE3A3A96`                                    |
| SHI     | neutral `#D4D4D496` and neutral light `#D4D4D428`       |

The pages are `territoryDecalSet` prototypes in `territory_decals.yml`: markings and hazard stripes, floor tiling,
borders and trims (steel brick tile in every piece and direction first, then the other trims), numbers and department
signs. A palette's `emblems` list adds a page of the faction's own insignia once there are tile-sized ones.

Painting needs the same thing building does - standing on held ground of your own faction - and reaches 10 tiles. A
tile takes up to 4 decals and a territory 4000. Painted markings are part of the grid, so a persistent territory keeps
them between rounds, and the next holder can scrub them off.

## Carrying a territory between rounds

Mark the territory's element in the gamemode's `gameMapsByID` as persistent:

```yaml
PsionicOutpost:
  gameMapID: RatPsionicOutpost
  posX: 3000
  posY: 1500
  pinned: true
  persistent: true
  persistentVersion: 1
```

The grid is then saved the moment the round ends and loaded back from that save next round, wherever this round's
layout puts it. Whatever was wrecked stays wrecked; whatever was built or repaired stays that way. Saves live in the
campaign's `persistent_pois/`, one file per gameMap plus the previous round's copy, keyed by gameMapID - every preset
of the same campaign that spawns the same gameMap shares one territory, and other campaigns keep their own copy.

Before saving, the round is cleared off the grid. Players, ghosts and anything with a mind never travel with it, docked
ships are cast off, and the following are deleted: corpses, mobs that were not there when the territory first loaded
(built works such as the anti-boarder turret carry MobState but are kept), spills, rounds in flight, and anything carried in during play that was left loose on the deck or stashed in storage.
Anchored construction and machine internals stay. Loot the territory came with stays until somebody takes it.

Bump `persistentVersion` after editing the map file. A save made against an older version is set aside and the
territory starts over from the new map. A save that fails to load is set aside the same way and logged.

```
persistentpoi list             every territory with a save, and whether it is loaded this round
persistentpoi reset <key>      start over from the map file next round
persistentpoi rollback <key>   put the previous round's save back in place of the latest one
```

Both work mid-round: the territory plays on as it is, but its round is not saved over the result when it ends.

## The sector's territories

About half the political map's 23 hexes hold a capturable territory - 12 of them, the same hexes in every Freeplay
preset and Hyperwar, all persistent (the layout is drawn in the comment at the top of
`_Crescent/GameRules/hullrotGamemodes.yml`). None stands in a hex with a station in it - Gliess Santo, the faction
stations, the Tribal Hideout; the rest of the map is open ground. The TAP preset leaves out the Clementine Wreck, whose corner hex is the Tribal Hideout's there.

**Fixed at the hex centres, off every field.** Each territory stands at its hex's centre, the same spot every round,
and at least 300 m clear of every asteroid belt and wreck field the round's worldgen lays out (every biome with chunk
bounds that places debris). Where a field reached a centre it was trimmed back instead: the Ship Graveyard's wreck
field stops at y = -1664 for the Gargut Wreck, and the field at 2000, 2000 starts at y = 1664 for the Abandoned Station.
A belt or wreck field may share a hex with a territory, never its ground.

They are copies of derelict maps, kept in `Maps/_Crescent/CapturablePois/` with their gameMaps in
`_Crescent/Maps/PointsOfInterest/CapturablePois/capturable_pois.yml`; the originals stay as they were, for the wreck
fields and whatever else spawns them. Each copy has only three things added, near the middle of its grid: the standard
(with `regionId` and `baseName`), a `WarpPoint` on the standard's tile named after the territory, and a derelict hunter
garrison (see below). Nothing else - consoles, guns, doors and power are the holder's to build from the build menu. The
gameMap's name, the standard's `baseName` and the warp point all carry the same name, and no two territories share one.
The warp point on the standard's tile then follows whatever radar shows - "DSM Gargut Wreck" once DSM holds it,
"DSM-Foundry" once they name it - and any other warp point the source map brought along under its old name (the Hayes
Derelict, the Psionic Church) is retired, so ghosts find each territory once, under its current name.

The six territories the sector started with are all in; six derelict copies fill the rest:

| gameMap                      | Map file (`CapturablePois/`) | Copied from                         | regionId            | Position       |
|------------------------------|------------------------------|-------------------------------------|---------------------|----------------|
| `CapturableAbandonedChapel`  | `abandonedchapel.yml`        | `Derelicts/psionicchurch.yml`       | `abandoned-chapel`  | -5000,  5000   |
| `CapturableCrystalSanctum`   | `psionicpoi.yml`             | (mapped as a capturable POI)        | `crystal-sanctum`   | -5000, -5000   |
| `CapturableExplorerWreck`    | `explorerwreck.yml`          | `Explorables/fogexplorer.yml`       | `explorer-wreck`    |  5000, -5000   |
| `CapturableSolarSailWreck`   | `solarsailwreck.yml`         | `Explorables/ruinedsolarsailor.yml` | `solar-sail-wreck`  | -5000,  2500   |
| `CapturableShuttleWreck`     | `shuttlewreck.yml`           | `Explorables/impwreck.yml`          | `shuttle-wreck`     |  5000, -2500   |
| `CapturableAbandonedStation` | `abandonedstation.yml`       | `Explorables/hayeswreck.yml`        | `abandoned-station` |  2500,  1250   |
| `CapturableClementineWreck`  | `clementinewreck.yml`        | `Derelicts/ClemWreck1.yml`          | `clementine-wreck`  |  5000,  5000   |
| `CapturableMojaveWreck`      | `mojavewreck.yml`            | `Derelicts/WereGood.yml`            | `mojave-wreck`      |  2500,  3750   |
| `CapturablePraxisWreck`      | `praxiswreck.yml`            | `Derelicts/PraxWreck1.yml`          | `praxis-wreck`      | -2500, -3750   |
| `CapturableNemesisWreck`     | `nemesiswreck.yml`           | `Derelicts/NemWreck1.yml`           | `nemesis-wreck`     | -2500,  1250   |
| `CapturableGargutWreck`      | `gargutwreck.yml`            | `Derelicts/GargWreck1.yml`          | `gargut-wreck`      | -2500, -1250   |
| `CapturableHammerheadWreck`  | `hammerheadwreck.yml`        | `Derelicts/HamWreck1.yml`           | `hammerhead-wreck`  |  2500, -1250   |

Eleven more copies are made and ready but laid out nowhere, for whenever a hex should be filled - add the gameMap to
each preset's `gameMapsByID` as persistent, the way the twelve are: the Stern, Lodka, Corvax, Molot, Hind, Suzerain,
Triumphant, Qian, Ivarod, Musiyane and Cockroach wrecks (`Capturable<Name>Wreck`, regionId `<name>-wreck`). Of the
derelict folder's wrecks each class has one copy (the variant with the most open floor); the Exhumer (almost no open
floor), the Radiation Manatee (it irradiates whoever holds it) and the smallest, the Gazpachov, have none.

`SectorLayoutTest` fails if an element goes back to a random spot, a territory stands within 300 m of a belt or
wreck field, a territory shares a hex with a station or another
territory, more than half the hexes hold one, a territory map lacks exactly one standard, or two territories share a
regionId or a name.

**Derelict garrisons.** Each territory map has two hunter spawners a few tiles from the standard: one sure
(`SpawnMobSoldierAIDerelictHunterRandom`) and one that turns up half the time (`...Chance`), so a territory starts with
one or two derelict hunters (`MobSoldierAIDerelictHunter*`, in `Mobs/NPCs/soldier_ai.yml`). They are the Saint's
Militia hunters with `SRMHunt` but not `SRM` - in their faction list and in their suit, since the Hunt's own suit adds
`SRM` as it is put on - and carry no ID card or headset, so they spare nobody, the Militia's own players included, and
every other side's soldiers and turrets treat them as the Hunt. They start hardsuited and on internals, never walk off their
wreck (`NpcGridBound`), and while no player is within 40 tiles they hold still (`NpcGarrisonDormancy`): they neither
breathe nor go hungry or thirsty, so they neither suffocate in vacuum nor starve in the hours - or rounds - before
anyone comes, and have full tanks when somebody does. A garrison is native to its territory, so survivors carry over
in its save; the dead are cleared out with every other corpse, and nothing brings them back.

**Drone pickets.** At round start every territory nobody holds gets one or two drones circling it 350 m out
(`TerritoryDronePickets` on the standard, flown by `TerritoryDronePicketSystem`). They are the faction drones' own
hulls - Valor, Rocinante, Wasp, Hornet, picked at random - that the Turning got to: they answer to no carrier and no drone
console, fly under a sickly purple IFF, and go by names like "Turnwracked Valor 3F-A1" (the first word comes from the
`TerritoryDroneNamePrefixes` dataset; the faction letters are gone). They show on radar but not on the shuttle
console's map (`IFFFlags.HideOnMap`), and their hulls' warp points are taken off, so ghosts can't warp to them. The
moment a ship comes within 500 m of the standard - anybody's, including whoever takes the ground mid-round: a hull
with working thrust and a deed, a real faction's colours or someone aboard - the picket goes for the nearest one, and
somebody out in a suit counts as well. It holds about 250 m off the target and now and then slides left or right
instead of circling it, and its aim wanders up to 20 m around the target (5 m for someone in a suit). They drop a
target once it is 650 m from the standard, and a drone that ends up past 850 m breaks off and flies home before it
picks a fight again. They hold fire while the territory itself is in the line of fire, so a ship docked alongside is
not worth wrecking the territory over. Asteroids, belts, wrecks, stations and other territories are never targets -
not even an asteroid with a miner on it, since only a grid with thrust counts. While no player body is within 1500 m
of the standard (`wakeRange`) the picket parks where it is and stops steering and aiming, so nobody pays for its
flight AI or physics while nobody can see it; it picks up again within a second of someone coming into range. Pickets are not part of the
territory: never saved, and one shot down stays down until the next round puts up a fresh one.

## The political map

Command mainframes (the **Political Map** tab) and shuttle consoles (the **POLITICS** tab) show the sector cut into
hexagons, each in the colour of the faction that rules it. The layout lives in
`_Crescent/PoliticalMap/political_map.yml`: flat-topped hexes 2500 apart, anchored on Gliess Santo, so Gliess Santo
and the four faction stations at 5000 on the axes each sit exactly in the middle of a hex - 23 hexes over the
10000 x 10000 sector.

A hex is ruled by whoever holds the most capturable territories (`PersistentCaptureRegionFlag`) in it; a tie leaves it
contested. With no held territory, the faction whose home station stands in it rules it (a map element spawned with
an `IFFFaction`). So putting a capturable POI in a hex is all it takes to make it conquerable: take the POI and the hex
turns your colour. Hovering a hex lists what stands in it; the legend counts how many hexes each faction rules.
Every hex carries a name - its column's letter west to east and its number north to south, like C3 - so a hex can be
called out over the radio.
