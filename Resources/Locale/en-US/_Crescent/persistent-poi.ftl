# # Permanent territory ground

persistent-poi-floor-tool-denied = The floor around the territory's standard is permanent ground. Only its holders can pry it up.
persistent-poi-structure-examine = [color=goldenrod]Territory works: it stays where it was built until it is destroyed or decommissioned.[/color]
persistent-poi-structure-unanchor-denied = Territory works are bolted into permanent ground.

# # Build action

persistent-poi-build-not-held = You can only build territory works on ground your faction holds.
persistent-poi-build-over-limit = Others of its kind went up while this was being built, and the territory has no room for it. It is taken down again and its materials handed back.
persistent-poi-build-category-defence = Defences
persistent-poi-build-category-command = Command
persistent-poi-build-category-production = Production
persistent-poi-build-category-power = Power & light
persistent-poi-build-category-doors = Doors & airlocks
persistent-poi-build-category-structure = Walls & windows
persistent-poi-build-category-atmos = Atmospherics
persistent-poi-build-category-furniture = Furniture
construction-condition-persistent-poi-held-ground = Must be built on territory your faction holds, standing on it.
construction-condition-persistent-poi-within-limit = The territory's level has no room for another of these. Raise its level at the administration console.
construction-condition-persistent-poi-capability = The territory is not tooled for this line of production. Retool it at the production console.

# # Development

persistent-poi-ui-level = Level { $level }

persistent-poi-category-targetingconsole = Fire-control consoles
persistent-poi-category-productionconsole = Production consoles
persistent-poi-category-administrationconsole = Administration consoles
persistent-poi-category-pointdefence = Point-defence guns
persistent-poi-category-antiboarderturret = Anti-boarder turrets
persistent-poi-category-factory = Production works
persistent-poi-category-generator = Generators
persistent-poi-category-radarconsole = Radar consoles
persistent-poi-category-shuttleconsole = Shuttle consoles
persistent-poi-category-spawnbeacon = Cryo spawners
persistent-poi-category-gearrack = Equipment racks

persistent-poi-admin-not-holder = Only the territory's holder can develop it.
persistent-poi-admin-max-level = The territory is already fully developed.
persistent-poi-admin-not-needed = The next level does not call for that.
persistent-poi-admin-already-enough = The next level already has all of that it needs.
persistent-poi-admin-deposited = Deposited { $amount } { $material } ({ $total }/{ $required }).
persistent-poi-admin-nothing-needed-in = Nothing in { THE($container) } is needed for the next level.
persistent-poi-admin-missing-materials = The next level still needs more materials.
persistent-poi-admin-upgraded = The territory is now level { $level }.

persistent-poi-admin-ui-title = Territorial Administration
persistent-poi-admin-ui-level = Level { $level }/{ $max }
persistent-poi-admin-ui-capability = Tooled for: { $capability }
persistent-poi-admin-ui-no-capability = Not tooled for production
persistent-poi-admin-ui-limits = What this level allows
persistent-poi-admin-ui-used = { $used }/{ $limit }
persistent-poi-admin-ui-used-unlimited = { $used }
persistent-poi-admin-ui-next-limit = next level: { $limit }
persistent-poi-admin-ui-top-level = The territory is fully developed.
persistent-poi-admin-ui-next-level = To reach level { $level }
persistent-poi-admin-ui-deposit-hint = Use material stacks on this console to deposit them, or a bag or box of them to hand over everything inside at once. It takes only what is still needed.
persistent-poi-admin-ui-upgrade = Raise to level { $level }
persistent-poi-admin-ui-deposited = { $deposited }/{ $required }

# # Production

persistent-poi-capability-general-ore = General ore extraction
persistent-poi-capability-general-ore-desc = Rigs bring up a mix of ores from below the territory: mostly steel, copper and coal, with some plasma, silver and gold.
persistent-poi-capability-targeted-ore = Targeted ore extraction
persistent-poi-capability-targeted-ore-desc = Rigs work one seam each, chosen per rig - far more of that one ore than a mixed rig gives.
persistent-poi-capability-textiles = Textiles
persistent-poi-capability-textiles-desc = Mills spin cotton and weave cloth and durathread.
persistent-poi-capability-materials = Materials
persistent-poi-capability-materials-desc = Foundries cast finished steel, glass, plastic and plasteel.
persistent-poi-capability-trade-goods = Trade goods
persistent-poi-capability-trade-goods-desc = Works crate alloys, gunparts, ammunition and plasma for export.

persistent-poi-production-status-running = Running
persistent-poi-production-status-disabled = Switched off
persistent-poi-production-status-no-recipe = Nothing to make
persistent-poi-production-status-unclaimed = Territory unclaimed
persistent-poi-production-status-unpowered = No power
persistent-poi-production-status-no-console = No production console running
persistent-poi-production-status-full = Storage full
persistent-poi-production-status-wrong-capability = Territory tooled for another line
persistent-poi-production-status-over-limit = Over the territory's limit
persistent-poi-production-status-starting = Spinning up
persistent-poi-production-status-low-power = Short of power: { $percent }% speed

persistent-poi-production-examine-capability = It belongs to the [color=cornflowerblue]{ $capability }[/color] line.
persistent-poi-production-examine-status = Status: [color=cornflowerblue]{ $status }[/color].
persistent-poi-production-examine-stored = It holds [color=cornflowerblue]{ $stored }/{ $max }[/color] finished batches.
persistent-poi-production-examine-power = It draws [color=orange]{ $kw } kW[/color] from the medium-voltage cable under it while it works.
persistent-poi-production-nothing-stored = There is nothing to dispense yet.
persistent-poi-production-round-over = The round is over and the territory has been saved. The works take no more orders until the next round.
persistent-poi-production-decommissioned = { CAPITALIZE($works) } is dismantled.
persistent-poi-production-output-entry = { $count }x { $item }

persistent-poi-production-ui-title = Territorial Production
persistent-poi-production-ui-capability = Line of production
persistent-poi-production-ui-tool = Tool for this line
persistent-poi-production-ui-retool = Retool
persistent-poi-production-ui-no-capability = The territory is not tooled for anything yet. Pick a line and tool for it; only its works can then be built.
persistent-poi-production-ui-factories = Works: { $count }/{ $limit }
persistent-poi-production-ui-factories-unlimited = Works: { $count }
persistent-poi-production-ui-hint = Works run on held ground while this console has power. Each draws heavily from medium-voltage cable laid under it, but only while it works; short of power it runs that much slower. Finished batches wait here until dispensed - and stay for whoever holds the territory next.
persistent-poi-production-ui-empty = No production works stand on this territory. Build some with the territory build action.
persistent-poi-production-ui-unclaimed = Unclaimed territory
persistent-poi-production-ui-holder = Held by { $faction }
persistent-poi-production-ui-count = { $count ->
    [one] 1 works
   *[other] { $count } works
}
persistent-poi-production-ui-output = Per batch: { $output }
persistent-poi-production-ui-stored = Stored { $stored }/{ $max }
persistent-poi-production-ui-turn-on = Switch on
persistent-poi-production-ui-turn-off = Switch off
persistent-poi-production-ui-dispense = Dispense
persistent-poi-production-ui-decommission = Decommission
persistent-poi-production-ui-decommission-confirm = Confirm?

persistent-poi-recipe-mixed-ore = Mixed ore
persistent-poi-recipe-steel-ore = Steel ore
persistent-poi-recipe-copper-ore = Copper ore
persistent-poi-recipe-plasma-ore = Plasma ore
persistent-poi-recipe-silver-ore = Silver ore
persistent-poi-recipe-gold-ore = Gold ore
persistent-poi-recipe-tungsten-ore = Tungsten ore
persistent-poi-recipe-quartz = Quartz
persistent-poi-recipe-uranium-ore = Uranium ore
persistent-poi-recipe-cotton = Cotton
persistent-poi-recipe-cloth = Cloth
persistent-poi-recipe-durathread = Durathread
persistent-poi-recipe-steel = Steel sheets
persistent-poi-recipe-glass = Glass sheets
persistent-poi-recipe-plastic = Plastic sheets
persistent-poi-recipe-plasteel = Plasteel sheets
persistent-poi-recipe-alloys = Crated alloys
persistent-poi-recipe-gunparts = Crated gunparts
persistent-poi-recipe-ammo = Crated ammunition
persistent-poi-recipe-plasma = Crated plasma

# # Fire control

targeting-console-alert-fire-control-overload = FIRE CONTROL OVERLOADED: { $count } GUNS PAST THE TERRITORY LIMIT OF { $limit } WILL NOT FIRE
point-cannon-examine-outside-fire-control = [color=red]It is past the territory's fire-control limit. No console will command it until the territory is developed further or an older gun is removed.[/color]

# # Faction mainframe territory tab

territory-console-list-heading = TERRITORIES
territory-console-tab-development = Development
territory-console-tab-production = Production
territory-console-list-empty = The sector knows of no territories.
territory-console-faction-cannot-hold = Your faction cannot hold territory. You can only watch who holds what.
territory-console-row = { $name } - level { $level }
territory-console-unclaimed = unclaimed
territory-console-status-held = Held - { $running }/{ $works } works, { $stored } waiting
territory-console-status-under-attack = UNDER ATTACK - { $attacker } at { $percent }%
territory-console-status-contested = CONTESTED - { $attacker } fighting at the standard
territory-console-status-other = Held by { $holder }
territory-console-status-not-loaded = Not in the sector this round - { $holder }
territory-console-nothing-selected = No territory selected
territory-console-pick-hint = Pick a territory from the list.
territory-console-not-held-details = Your faction does not hold this territory. Take it to run it from here.
territory-console-not-loaded-details = This territory is not in the sector this round. Its level and holder carry over to the round it returns.
territory-console-production-linked = Production link: online
territory-console-production-unlinked = Production link: no powered production console on the territory
territory-console-no-production-link = The territory has no powered production console to carry the order out.
territory-console-recipe-locked = That works has batches waiting. Dispense them at the territory before switching its recipe.
territory-console-not-member = This console answers to its own faction's people only.
territory-console-command-denied = Only your faction's command can give that order.
territory-console-command-locked = Defence orders, fire control, the name and giving the territory up are for your faction's command.
persistent-poi-production-ui-remote-hint = Orders go through the territory's production console. Dispensing and dismantling works can only be done there.
persistent-poi-production-ui-remote-recipe-locked = Batches are waiting at the works. Dispense them at the territory before switching its recipe.
persistent-poi-admin-ui-remote-deposit-hint = Materials are deposited, and the level raised, only at the territory's own administration console.

territory-console-tab-command = Command
territory-console-command-defence = DEFENCE
territory-console-command-fire-control = Fire control: { $powered }/{ $consoles } consoles powered
territory-console-command-fire-control-none = Fire control: no fire-control console on the territory
territory-console-command-manned = Manned - an operator holds the guns by hand
territory-console-command-engaging = Engaging: { $target }
territory-console-command-idle = No target
territory-console-command-guns = Ship guns: { $guns }/{ $limit } under fire control
territory-console-command-guns-unlimited = Ship guns: { $guns }
territory-console-command-turrets = Anti-boarder turrets: { $turrets }
territory-console-command-auto = Automatic fire control
territory-console-command-auto-desc = With nobody at the console, the battery engages on its own.
territory-console-command-zone = Hold exclusion zone
territory-console-command-zone-desc = Engage every non-ally that closes in, not only factions at war with you.
territory-console-command-turrets-armed = Anti-boarder turrets armed
territory-console-command-turrets-desc = Turrets shoot boarders hostile to your faction.
territory-console-command-on = ON
territory-console-command-off = OFF
territory-console-command-not-loaded = The territory is not in the sector this round. Defence orders wait until it is.
territory-console-command-name = NAME
territory-console-command-name-hint = Shown on radar, the political map and in under-attack alerts. Always starts with your faction.
territory-console-command-name-placeholder = Name it, or leave empty for its own name
territory-console-command-rename = Rename
territory-console-command-reset-name = Reset
territory-console-command-abandon-heading = GIVE IT UP
territory-console-command-abandon-desc = Strike your colours and leave the ground unclaimed. The territory may lose development as if it were taken from you.
territory-console-command-abandon = Abandon territory
territory-console-command-abandon-confirm = Press again to abandon

# # Territory decal painter

persistent-poi-build-decals = Paint floor markings
territory-decal-window-title = Territory markings
territory-decal-window-hint = Left click paints a tile, right click scrubs it. Hold to drag. Close to stop.
territory-decal-window-colour = Colour
territory-decal-window-rotate = Rotate
territory-decal-window-facing = Facing { $degrees }°
territory-decal-page-emblems = Emblems
territory-decal-page-markings = Markings
territory-decal-page-tiling = Tiling
territory-decal-page-borders = Borders & trims
territory-decal-page-signs = Numbers & signs
territory-decal-tile-full = That tile cannot take another marking. Scrub it first.
territory-decal-grid-full = The territory is covered in markings. Scrub some before painting more.

# # Shipyard and dockyard

persistent-poi-build-category-shipyard = Shipyard & dockyard
persistent-poi-build-category-deployment = Deployment
persistent-poi-category-shipyard = Shipyards
persistent-poi-category-dockingport = External airlocks
construction-condition-persistent-poi-faction = Only its own faction can build this.

# # Remote fire control

territory-console-command-remote-fire = Take fire control
territory-console-command-remote-fire-hint = Opens the territory's targeting console here. Leave this tab and it goes back to automatic.
territory-console-command-remote-fire-unavailable = Needs a powered fire-control console of yours on the territory.
territory-remote-fire-no-console = The territory has no fire-control console of yours to man.
territory-remote-fire-unpowered = The territory's fire-control console has no power.

# # Paid production orders

territory-payment-pay-from = Pay from:
territory-payment-personal-button = Personal ({ $balance } cr)
territory-payment-personal-none = Personal (no account)
territory-payment-faction-button = { $faction } treasury ({ $allowance } cr)
territory-payment-faction-none = Faction treasury (not yours)
territory-payment-personal-note = Paid out of your own pocket.
territory-payment-faction-note = Treasury: { $balance } cr - you may spend { $percent }% of it per round.
territory-payment-source-personal = your account
territory-payment-source-faction = the faction treasury
territory-payment-no-account = You have no bank account to pay from.
territory-payment-personal-short = You cannot afford that.
territory-payment-not-member = Only the holding faction's own people can spend its treasury.
territory-payment-treasury-short = The treasury cannot cover that.
territory-payment-share-spent = You have spent your share of the treasury for this round.

persistent-poi-rush-button = Rush - { $price } cr
persistent-poi-rush-button-idle = Rush
persistent-poi-rush-tooltip = Finish the current batch now. Costs what the rest of it would sell for, and then some.
persistent-poi-rush-unavailable = That works is not running, so there is no batch to rush.
persistent-poi-rush-done = Batch finished. { $price } cr paid from { $source }.

persistent-poi-overtime-off = Ordinary hours. Overtime runs every works x{ $speed }.
persistent-poi-overtime-active = OVERTIME x{ $speed } - { $time } left
persistent-poi-overtime-button = Overtime +{ $minutes } min - { $price } cr
persistent-poi-overtime-button-idle = Overtime +{ $minutes } min
persistent-poi-overtime-button-maxed = Overtime booked to the limit
persistent-poi-overtime-unavailable = No works on the territory are running, so overtime would buy nothing.
persistent-poi-overtime-maxed = Overtime is already booked as far ahead as it goes.
persistent-poi-overtime-done = Overtime booked. { $price } cr paid from { $source }.

# # Build menu

persistent-poi-build-ui-title = Territory Works
persistent-poi-build-ui-search = Search works...
persistent-poi-build-ui-paint-tooltip = Paint markings onto the floor in your faction's colours.
persistent-poi-build-ui-no-territory = Not on held ground
persistent-poi-build-ui-summary = Level { $level }/{ $max } · { $line }
persistent-poi-build-ui-line = Tooled for { $line }
persistent-poi-build-ui-no-line = Not tooled for production
persistent-poi-build-ui-no-match = Nothing matches that.
persistent-poi-build-ui-empty-page = Nothing here for your faction.
persistent-poi-build-ui-pick = Pick something to build.
persistent-poi-build-ui-stages = What it takes
persistent-poi-build-ui-stage-first = { $number }. Raise the ghost with
persistent-poi-build-ui-stage-next = { $number }. Then use on the frame
persistent-poi-build-ui-carried = you have { $count }
persistent-poi-build-ui-carried-split = you have { $count }, in small stacks
persistent-poi-build-ui-place = Place
persistent-poi-build-ui-footer = Placing costs nothing. Each stage takes its material from one stack in your hands, your bags or the floor beside you - merge stacks that are too small.
persistent-poi-build-ui-short-not-held = not held
persistent-poi-build-ui-short-wrong-line = other line
persistent-poi-build-ui-short-level = level { $level }
persistent-poi-build-ui-short-full = full { $count }/{ $limit }
persistent-poi-build-ui-short-used = { $count }/{ $limit }
persistent-poi-build-ui-status-ready = [color=#7FD67A]Ready to place[/color]
persistent-poi-build-ui-status-not-held = [color=#E05A5A]Only on ground your faction holds[/color]
persistent-poi-build-ui-status-wrong-line = [color=#E05A5A]Needs the territory tooled for { $line }[/color]
persistent-poi-build-ui-status-level = [color=#E05A5A]Needs level { $level }[/color]
persistent-poi-build-ui-status-full = [color=#E05A5A]The territory has all it can take[/color]
persistent-poi-build-ui-status-full-next = [color=#E05A5A]Full - level { $level } allows more[/color]
persistent-poi-build-ui-unlimited = No limit on these.
persistent-poi-build-ui-limit = { $category }: { $count }/{ $limit } at this level
persistent-poi-build-ui-limit-unlimited = { $category }: { $count }, no limit

# # Territorial doors

territory-access-examine-open = [color=goldenrod]A territorial door. The ground is unclaimed, so it opens for anybody.[/color]
territory-access-examine-held = [color=goldenrod]A territorial door. It opens only for { $faction }.[/color]

# # Deployment

territory-holder-only-denied = Only the faction holding this ground can use this.
territory-spawn-beacon-examine-online = [color=green]Online.[/color] Your faction's people can choose to wake up here when they join.
territory-spawn-beacon-examine-offline = [color=red]Offline.[/color] It needs power, a fully developed territory and a holder.
territory-spawn-beacon-unavailable = The territory's cryo spawner could not take you, so you woke up at home instead.
territory-spawn-beacon-arrived = You wake up in the cryo spawner at { $territory }.
late-join-deploy-label = Deploy at:
late-join-deploy-home = Home station
