# # Eclipsion - Soot Contingency Protocol

# Shown to the muted player
soot-protocol-activated = SOOT CONTINGENCY PROTOCOL ACTIVE. You can no longer use OOC, LOOC, dead chat, radio, PDA messages or write anything down. Only emoji and emotes are allowed, and whatever you try to say out loud will come out a lot nicer. Duration: {$duration}. Reason: {$reason}
soot-protocol-deactivated = The Soot Contingency Protocol has been lifted. You can chat again.
soot-protocol-expired = The Soot Contingency Protocol has expired. You can chat again.
soot-protocol-blocked = Soot Contingency Protocol is active: you may only send emoji or emotes.
soot-protocol-blocked-writing = Soot Contingency Protocol is active: you may only write emoji.

# What comes out instead when they try to talk
soot-protocol-phrase-1 = I love you.
soot-protocol-phrase-2 = I like your hat.
soot-protocol-phrase-3 = Hello, my name is Soot. I am a nice person.
soot-protocol-phrase-4 = You look wonderful today!
soot-protocol-phrase-5 = I think everyone here is doing a great job.
soot-protocol-phrase-6 = Has anyone ever told you that you are amazing?
soot-protocol-phrase-7 = I would like to give everyone a big hug.
soot-protocol-phrase-8 = What a lovely day it is!
soot-protocol-phrase-9 = I appreciate you, friend.
soot-protocol-phrase-10 = Your shoes look very nice.
soot-protocol-phrase-11 = I promise to always be kind and polite.
soot-protocol-phrase-12 = I love the admins very much.
soot-protocol-phrase-13 = Thank you for being my friend.
soot-protocol-phrase-14 = You have a beautiful smile.
soot-protocol-phrase-15 = I just want everyone to be happy.
soot-protocol-phrase-16 = Hello! I am Soot and I love puppies.
soot-protocol-phrase-17 = Sharing is caring!
soot-protocol-phrase-18 = I baked cookies for everyone. They are made of love.
soot-protocol-phrase-19 = You are all my best friends.
soot-protocol-phrase-20 = I like your haircut, it really suits you.
soot-protocol-phrase-21 = Please have a wonderful shift!
soot-protocol-phrase-22 = I believe in you!
soot-protocol-phrase-23 = Rainbows make me very happy.
soot-protocol-phrase-24 = Can I have a hug, please?
soot-protocol-phrase-25 = I am so grateful to be here with all of you.

# adminfun, said at random every now and then
soot-protocol-fun-1 = Thank you!
soot-protocol-fun-2 = Yes, of course.
soot-protocol-fun-3 = Spread love.
soot-protocol-fun-4 = Thank you so much.
soot-protocol-fun-5 = Of course, friend!
soot-protocol-fun-6 = Spread love, not hate.
soot-protocol-fun-7 = Yes! Absolutely!
soot-protocol-fun-8 = Thank you, admins.
soot-protocol-fun-9 = Yes, of course, whatever you need.
soot-protocol-fun-10 = Love and peace, everyone.
soot-protocol-fun-11 = Spread love!
soot-protocol-fun-12 = Thank you for your service.
soot-protocol-fun-13 = Yes, of course!
soot-protocol-fun-14 = Hugs for everyone!
soot-protocol-fun-15 = Thank you, thank you, thank you.
soot-protocol-fun-16 = Be kind. Spread love.

# Command
cmd-sootcontingencyprotocol-desc = Blocks a player from every chat channel (OOC, LOOC, dead chat, radio, PDA) and from writing, leaving only emoji and emotes. Anything they try to say out loud comes out as a nice sentence.
cmd-sootcontingencyprotocol-help = Usage: sootcontingencyprotocol activate <player> [minutes, 0 = until deactivated] [reason...]
    sootcontingencyprotocol deactivate <player>
    sootcontingencyprotocol status <player>
    sootcontingencyprotocol list
    sootcontingencyprotocol adminfun <player> [on|off]
cmd-sootcontingencyprotocol-player-not-found = Couldn't find a player called {$player}.
cmd-sootcontingencyprotocol-server = Server
cmd-sootcontingencyprotocol-no-reason = none given
cmd-sootcontingencyprotocol-permanent = until deactivated
cmd-sootcontingencyprotocol-minutes = {$minutes} {$minutes ->
    [one] minute
    *[other] minutes
}
cmd-sootcontingencyprotocol-activated = Soot Contingency Protocol activated on {$player} ({$duration}).
cmd-sootcontingencyprotocol-deactivated = Soot Contingency Protocol deactivated on {$player}.
cmd-sootcontingencyprotocol-not-active = {$player} is not under the Soot Contingency Protocol.
cmd-sootcontingencyprotocol-admin-activated = {$admin} activated the Soot Contingency Protocol on {$player} ({$duration}).
cmd-sootcontingencyprotocol-admin-deactivated = {$admin} deactivated the Soot Contingency Protocol on {$player}.
cmd-sootcontingencyprotocol-list-empty = Nobody is under the Soot Contingency Protocol.
cmd-sootcontingencyprotocol-list-header = {$count} player(s) under the Soot Contingency Protocol:
cmd-sootcontingencyprotocol-entry = {$player} - by {$admin}, remaining: {$remaining}, reason: {$reason}{$adminfun ->
    [true] , adminfun on
    *[other] {""}
}
cmd-sootcontingencyprotocol-adminfun = Soot Contingency Protocol adminfun is now {$state} for {$player}.
cmd-sootcontingencyprotocol-admin-adminfun = {$admin} turned Soot Contingency Protocol adminfun {$state} for {$player}.
cmd-sootcontingencyprotocol-adminfun-not-active = {$player} is not under the Soot Contingency Protocol. Activate it first.
cmd-sootcontingencyprotocol-adminfun-on = on
cmd-sootcontingencyprotocol-adminfun-off = off
cmd-sootcontingencyprotocol-hint-mode = <activate|deactivate|status|list>
cmd-sootcontingencyprotocol-hint-activate = Mute the player, emoji only
cmd-sootcontingencyprotocol-hint-deactivate = Lift the mute
cmd-sootcontingencyprotocol-hint-status = Show whether the player is muted
cmd-sootcontingencyprotocol-hint-list = List all muted players
cmd-sootcontingencyprotocol-hint-adminfun = Make the muted player say nice things on their own every now and then
cmd-sootcontingencyprotocol-hint-adminfun-state = [on|off, empty toggles]
cmd-sootcontingencyprotocol-hint-player = <player>
cmd-sootcontingencyprotocol-hint-minutes = [minutes, 0 = until deactivated]
cmd-sootcontingencyprotocol-hint-reason = [reason]
