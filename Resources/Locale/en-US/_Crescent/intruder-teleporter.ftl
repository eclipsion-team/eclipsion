intruder-teleporter-title = Intruder Teleporter

# Sections
intruder-teleporter-section-roster = BOARDING PARTY
intruder-teleporter-section-roster-count = BOARDING PARTY [{ $count }/{ $max }]
intruder-teleporter-section-target = TARGET
intruder-teleporter-section-orders = ORDERS

# Roster
intruder-teleporter-roster-entry = { $name }
intruder-teleporter-roster-entry-lead = { $name } (LEAD)
intruder-teleporter-roster-empty-hint = No one signed on.
intruder-teleporter-join = Sign on
intruder-teleporter-leave = Sign off
intruder-teleporter-joined = You sign onto the boarding roster.
intruder-teleporter-left = You sign off the boarding roster.
intruder-teleporter-roster-full = The roster is full ({ $max }).
intruder-teleporter-roster-locked = The roster is locked, the order is already out.
intruder-teleporter-roster-empty = No one is signed onto the roster.

# Target readout
intruder-teleporter-target-none = No target
intruder-teleporter-target-pick-hint = Click a hostile contact on the scanner. Boarding range is { $range } m.
intruder-teleporter-target-distance = { $distance } m / { $range } m
intruder-teleporter-no-target-there = No hostile vessel at that bearing.

intruder-teleporter-status-valid = Lock acquired.
intruder-teleporter-status-outofrange = Out of boarding range.
intruder-teleporter-status-nothostile = Not a hostile vessel.
intruder-teleporter-status-lost = Contact lost.
intruder-teleporter-status-none = No target selected.

# Orders
intruder-teleporter-launch = LAUNCH BOARDING PARTY
intruder-teleporter-launch-inflight = LAUNCHING ({ $seconds })
intruder-teleporter-abort = Abort
intruder-teleporter-access-hint = Aiming and launch require command access.

intruder-teleporter-state-ready = Ready. The target is warned { $seconds } seconds before arrival.
intruder-teleporter-state-launching = { $target }: boarding party arrives in { $seconds } s.
intruder-teleporter-state-cooldown = Bluespace coils recharging: { $seconds } s.
intruder-teleporter-state-no-access = Insufficient access to give the order.
intruder-teleporter-state-no-squad = Nobody is signed onto the roster.
intruder-teleporter-state-no-target = No valid target locked.

intruder-teleporter-hint = One way. The party has to find its own way home.

# Refusals
intruder-teleporter-access-denied = Access denied.
intruder-teleporter-already-launching = An order is already out.
intruder-teleporter-on-cooldown = Bluespace coils still recharging: { $seconds } s.
intruder-teleporter-unpowered = The console is unpowered.

# Squad feedback
intruder-teleporter-squad-countdown = The projector locks on. You are thrown in { $seconds } seconds.
intruder-teleporter-abort-manual = The boarding order was called off.
intruder-teleporter-abort-power = The projector loses power. The boarding order is off.
intruder-teleporter-abort-target-lost = The target slipped the lock. The boarding order is off.
intruder-teleporter-left-behind = You were not aboard when the projector fired. You missed the drop.
intruder-teleporter-no-drop-point = The projector could not find anywhere safe to put you down.
intruder-teleporter-contained = The projector cannot lock onto you inside anything. Step out onto the deck to make the drop.

# What the target vessel hears
intruder-teleporter-warning-sender = Bluespace Proximity Alarm
intruder-teleporter-warning = Hostile bluespace signature locked onto this vessel. Intruders inbound in { $seconds } seconds.
intruder-teleporter-warning-source = Hostile bluespace signature locked onto this vessel from { $vessel }. Intruders inbound in { $seconds } seconds.
intruder-teleporter-warning-final = Bluespace signature resolving. Intruders in { $seconds } seconds.
intruder-teleporter-warning-final-source = Bluespace signature from { $vessel } resolving. Intruders in { $seconds } seconds.
intruder-teleporter-warning-cleared = Hostile bluespace signature lost. No boarding is inbound.
intruder-teleporter-warning-arrived = Bluespace translation complete. { $count } intruders are aboard.
