## Verbs

npc-squad-verb-recruit = Recruit to squad
npc-squad-verb-dismiss = Dismiss from squad

npc-squad-recruit-fail-incapacitated = You are in no state to lead anyone.
npc-squad-recruit-fail-taken = {CAPITALIZE(THE($npc))} already follows someone else.
npc-squad-recruit-fail-faction = {CAPITALIZE(THE($npc))} won't take orders from you.
npc-squad-recruit-fail-full = Your squad is full. ({$max} max)

## Popups

npc-squad-recruited = {CAPITALIZE(THE($npc))} joins your squad.
npc-squad-dismissed = {CAPITALIZE(THE($npc))} leaves your squad.
npc-squad-member-died = {CAPITALIZE(THE($npc))} is down!
npc-squad-focus = Your squad focuses fire on {THE($target)}.

npc-squad-order-given-follow = Squad: follow me!
npc-squad-order-given-defend = Squad: hold this position!
npc-squad-order-given-attack = Squad: attack!
npc-squad-order-given-holdfire = Squad: hold fire!
npc-squad-order-given-fortify = Squad: barricade us in!

npc-squad-formation-given-loose = Squad: spread out, stay close.
npc-squad-formation-given-column = Squad: single file!
npc-squad-formation-given-staggered = Squad: staggered column!
npc-squad-formation-given-line = Squad: form a line!
npc-squad-formation-given-wedge = Squad: wedge on me!
npc-squad-formation-given-circle = Squad: circle up!

npc-squad-fort-no-floor = There's no floor here to barricade.
npc-squad-fort-short = Your squad only has steel for {$have} of the {$need} barricades.
npc-squad-fort-done = Barricades up: {$built}/{$total}.

npc-squad-killall-given-on = Squad: weapons free - anyone not ours or allied is fair game!
npc-squad-killall-given-off = Squad: known enemies only!

npc-squad-hold-move = Squad: move up and hold there!
npc-squad-hold-move-too-far = Nobody holding is within {$range} tiles of that spot.

## Orders

npc-squad-order-follow = Follow
npc-squad-order-defend = Defend
npc-squad-order-attack = Attack
npc-squad-order-holdfire = Hold fire
npc-squad-order-fortify = Build cade

npc-squad-order-follow-desc = Stay close and fight anything that gets near me.
npc-squad-order-defend-desc = Each of you, dig in right where you stand and fight from there. Nobody moves.
npc-squad-order-attack-desc = Engage every enemy in sight and push onto them.
npc-squad-order-holdfire-desc = Stay exactly where you are and don't start anything. Point at a spot within 10 tiles to move there and hold.
npc-squad-order-fortify-desc = Barricade in the 3x3 square around me, leaving one way in, then hold inside it. Uses the squad's steel.

## Rules of engagement

npc-squad-ui-engagement = Rules of engagement:

npc-squad-killall-on = Kill all
npc-squad-killall-off = Enemies only

npc-squad-killall-on-desc = Engage anyone who is neither one of us nor wearing an allied faction's ID: neutrals, unaligned boarders, people without an ID.
npc-squad-killall-off-desc = Engage only the factions we are hostile to, and anyone who shoots at us.

## Formations

npc-squad-ui-formation = Formation while following:

npc-squad-formation-loose = Loose
npc-squad-formation-column = Column
npc-squad-formation-staggered = Staggered
npc-squad-formation-line = Line
npc-squad-formation-wedge = Wedge
npc-squad-formation-circle = Circle

npc-squad-formation-loose-desc = No set places: stay near me.
npc-squad-formation-column-desc = Single file behind me. For corridors.
npc-squad-formation-staggered-desc = Two staggered files behind me, one each side.
npc-squad-formation-line-desc = Abreast of me, spread out to both sides.
npc-squad-formation-wedge-desc = A V behind me, with me at the tip.
npc-squad-formation-circle-desc = A ring around me, every way covered.

## Window

npc-squad-ui-title = Squad Command
npc-squad-ui-count = Squad: {$count}/{$max}
npc-squad-ui-squad-orders = Orders for the whole squad:
npc-squad-ui-point-hint = Point at anyone to have the squad attack them - and their faction, for a while. Point at the floor to move soldiers holding within 10 tiles there.
npc-squad-ui-empty = Nobody is following you. Right-click a soldier of your side to recruit them.
npc-squad-ui-dismiss-all = Dismiss all
npc-squad-ui-dismiss = Dismiss
npc-squad-ui-in-combat = In combat
npc-squad-ui-magazines = Mags: {$count}
npc-squad-ui-distance = {$distance} m
npc-squad-ui-distance-unknown = far away
npc-squad-ui-current-order = Order: {$order}

npc-squad-condition-healthy = Healthy
npc-squad-condition-wounded = Wounded
npc-squad-condition-critical = Critical
npc-squad-condition-dead = Dead

## Soldier AI

npc-soldier-barricade-built = {CAPITALIZE(THE($npc))} finishes putting up a barricade.
npc-no-loot-dust = {CAPITALIZE(THE($npc))} crumbles to dust, gear and all.
