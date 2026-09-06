# --- Console / UI ---
faction-roundend-window-title = Faction Directives
faction-roundend-no-missions = No directives are configured on this terminal.
faction-roundend-turn-in = Submit directive
faction-roundend-status-completed = ✔ Directive fulfilled
faction-roundend-denied = Access denied — only faction command may submit directives here. Consult your superiors.
faction-roundend-item-accepted = Supplies accepted and counted toward the directive. They cannot be returned.
faction-roundend-incomplete = The directive's materiel quota has not been met yet.
faction-roundend-completed = Directive fulfilled. The sector has been notified.
faction-roundend-examine = Use the requested items on the terminal to submit them. [color=red]Submitted items are consumed.[/color] You need [color=yellow]faction command credentials[/color] to complete a directive.
faction-roundend-announcer = Sector Command

# DSM (steals from NCWL)
faction-mission-item-league-prize = Commandant's ID card [color=gray]or[/color] League Plans

faction-mission-dsm-name = Convince Count Olywier
faction-mission-dsm-desc =
    PRIORITY DIRECTIVE
    {""}
    Count Olywier will send reinforcements once we can show him a victory in Taypan.
    {""}
    Capture the Commandant's ID card or seize the League Plans. Either will give him the proof he wants.
    {""}
    Negotiate, steal it, or take it by force. Deliver one of these items to the terminal intact.

faction-mission-dsm-announce = Count Olywier has accepted the evidence. Imperial reinforcements are on their way, and the League now knows nowhere is beyond the Empire's reach.

faction-mission-dsm-sender = Imperial High Command


# NCWL (steals from DSM)
faction-mission-item-imperial-prize = Lord Admiral's ID card [color=gray]or[/color] Ancient Imperial Secrets

faction-mission-ncwl-name = Prove the Revolution Advances
faction-mission-ncwl-desc =
    CENTRAL COMMITTEE DIRECTIVE
    {""}
    Chengridz is holding back reinforcements until we can show that the Imperial position in Taypan is weakening.
    {""}
    Capture the Lord Admiral's ID card or recover the folder marked Ancient Imperial Secrets. It contains Imperial plans and strategic records.
    {""}
    Deliver either item to the terminal intact. Command has approved negotiation, infiltration, and armed seizure.

faction-mission-ncwl-announce = The evidence has reached Chengridz. The League recognizes our victories in Taypan, and reinforcements are already on their way.

faction-mission-ncwl-sender = Chengridz Central Command

# --- Taypan tonnage charters (SHI + TFSC) ---
faction-mission-item-charter-mandate = Taypan tonnage charter, Mandate seal
faction-mission-item-charter-league = Taypan tonnage charter, League stamp

faction-mission-empire-name = Convince the Empire
faction-mission-empire-desc =
    HIGH COMMAND DIRECTIVE
    {""}
    Our heavy ships need tonnage permits from both the Empire and the Workers' League before they can enter Taypan. This terminal accepts the charter bearing the Mandate seal.
    {""}
    You may offer discounts, equipment, or ships in exchange for the charter. Command has authorized these expenses.
    {""}
    If talks fail, look for the document in the Imperial senior officers' offices. Bring it back intact. We will still need the League's permit.
faction-mission-empire-announce = The Divine Sol Mandate has approved a Taypan tonnage charter. Armed merchant vessels may now enter the sector under the Mandate seal.
faction-mission-empire-sender = Outer Rim Prefecture

faction-mission-league-name = Convince the Communists
faction-mission-league-desc =
    HIGH COMMAND DIRECTIVE
    {""}
    Our heavy ships need tonnage permits from both the Empire and the Workers' League before they can enter Taypan. This terminal accepts the charter bearing the League stamp.
    {""}
    You may offer discounts, equipment, or ships in exchange for the charter. Command has authorized these expenses.
    {""}
    If talks fail, look for the document in the League senior officers' offices. Bring it back intact. We will still need the Mandate's permit.
faction-mission-league-announce = The Workers' League Oversight Committee has approved a Taypan tonnage permit for armed merchant vessels. The vote has been entered in the public record.
faction-mission-league-sender = Commissariat of Shipping

# --- CMM ---
faction-mission-item-authkey = Analiesse auth key

faction-mission-cmm-name = Recover the Analiesse Key
faction-mission-cmm-desc =
    MINUTEMEN COMMAND DIRECTIVE
    {""}
    Long-range scans picked up a hull matching the Analiesse, years after she was reported lost. Her command authentication key may still be aboard.
    {""}
    Board the wreck, recover the key, and bring it to the terminal. Other crews may have picked up the same signal.
    {""}
    Keep the key's purpose off open channels. Command will handle it when you return.
faction-mission-cmm-announce = The Colonial Minutemen have recovered the Analiesse authentication key. Minutemen Command has taken custody of it.
faction-mission-cmm-sender = Minutemen Command


# ============================================================================
# Faction finale beats. Broadcast to everyone in bold orange (server chat
# style) once a faction has completed EVERY directive on its terminal.
# ============================================================================

faction-finale-dsm = A massive Imperial signal has appeared on shuttle sensors: COUNTSMAN. Count Olywier has approved reinforcements and is moving the mobile base into Taypan.

faction-finale-ncwl = Shuttle sensors have picked up DEAR CLEMENTINE, the Communard cruiser that served during the construction of Balreska. League High Command has committed her to Taypan.

faction-finale-tfsc = The TFCF secured enough contracts, access, and common support to bring the JACKAL into Taypan. The Federation now has a platform capable of defending its independent market on its own terms. The commercial war has entered a new phase.

faction-finale-shi = Shuttle sensors have picked up AMATERASU. With permission from both major powers, Shinohara is moving its mobile base into Taypan.

faction-finale-cmm = The Colonial Minutemen recovered enough of Analiesse's archive and command authentication to restore the dream it represented. Imperial forces destroyed the old mobile headquarters after the CMM refused to turn local law into Imperial obedience. Your shuttle console now detects ANALIESSE again. Taypan is not saved, but it may once more have a public patrol force beyond Gliess Santo.


# ============================================================================
# Conquest victories. Broadcast in bold orange when one alliance bloc is all
# that is left standing, or when the round runs out with no victor.
# ============================================================================

# Fired the moment the war looks settled — the losers still have this long to
# retake a station's banners and call the whole thing off.
faction-victory-pending =
    THE WAR IS ALL BUT OVER. Every seat of power still flying its own colours belongs to { $factions }.
    {""}
    Nothing is signed yet. Anyone who can retake a fallen station's banners in the next { $minutes } minutes puts their flag back in the war — and this declaration is torn up. After that, Taypan is settled.

faction-victory-cancelled = Belay that. A station's banners have been torn down and its own colours raised again — its flag is back in the war. The declaration is withdrawn. Taypan is still contested.

# --- Round end summary screen ---
faction-conquest-summary-winner = [color=orange]The war for Taypan was won by: { $factions }.[/color]
faction-conquest-summary-nobody = [color=orange]The war for Taypan was never settled. Nobody took the sector.[/color]
faction-conquest-summary-station-standing = [color=green]{ $station }[/color] ({ $faction }) was still standing.
faction-conquest-summary-station-fallen = [color=red]{ $station }[/color] ({ $faction }) fell.

faction-victory-cmm = CMM VICTORY — LOCAL ARTICLES PREVAIL. Public patrols and station law hold against every outside claimant.

faction-victory-gs = GLIESS VICTORY. The Sheriff's forces have secured Gliess Santo and its independence from the sector's rival powers.

faction-victory-dsm = DSM VICTORY — THE CHARTER HOLDS. Count Olywier's seal governs the frontier, and notice of the victory travels to Crown institutions in Domain.

faction-victory-ncwl = NCWL VICTORY — THE FRONT ADVANCES. The winning party's program goes to Chengridz and Kane for ratification while the Workers' Union secures the gains.

faction-victory-shi = SHI VICTORY. Shinohara controls the sector, its shipyards, and its trade routes.

faction-victory-tfsc = TFCF VICTORY. The Federation's member organizations have secured their independence and control of trade in Taypan.

faction-victory-tap = TAP VICTORY — THE OLD CLAIMS ENDURE. The tribes keep their sanctuary and the void beyond it answers to no foreign throne.

faction-victory-timeout = No one won. The wars ground on, the banners kept falling, and no flag secured Taypan. Another Turning renewed exposed fields, erased temporary gains, and made the frontier profitable enough to fight over again.


# ============================================================================
# Conquest banners. The capture warning fires the moment a station's last banner
# falls to the enemy and its shields collapse; the infestation announcement fires
# once the ten-minute warning expires and the Turning enters the hull.
# ============================================================================

# Popups shown at the banner itself when it changes hands.
conquest-flag-captured = { $faction } has seized the banner!
conquest-flag-reclaimed = { $faction } has torn the banner down and raised their own!

# Popups while someone works a banner. Clicking it starts a do-after; moving or taking a hit cancels it.
conquest-flag-capture-begin-self = You take hold of the banner and start hauling it down. Stand your ground.
conquest-flag-capture-begin-others = { $faction } is hauling the banner down!
conquest-flag-already-working = You are already working at this banner.
conquest-flag-already-yours = Your colours already fly here.
conquest-flag-no-faction = This claim means nothing to you.
conquest-flag-grace = The claim has not settled yet — { $seconds } seconds before it can be contested.

# Examine lines on a banner.
conquest-flag-examine-home = It flies the colours of [color=yellow]{ $faction }[/color].
conquest-flag-examine-held-home = [color=green]Held by its own faction ({ $faction }).[/color]
conquest-flag-examine-held-enemy = [color=red]Seized by { $faction }.[/color]
conquest-flag-examine-capturing = [color=orange]{ $faction } is hauling it down right now.[/color]
conquest-flag-examine-grace = [color=gray]The claim has not settled — { $seconds } seconds until it can be contested.[/color]
conquest-flag-examine-hint = Use it and hold still for { $seconds } seconds to raise your own colours here.

# Sector-wide warning the moment a station's LAST banner is taken and its clock starts.
faction-station-captured = SHIELD FAILURE — { $captor } forces have raised their banners over { $station } ({ $faction }) and the station's outer protection has collapsed. Retake her within { $minutes } minutes. If the shields are not restored, the Turning will breach the hull and claim the station.

faction-station-capture-cancelled = CONTAINMENT RESTORED — { $station } ({ $faction }) has been reclaimed before the Turning could enter the hull. Its outer protection is coming back online.

faction-station-fall-aurora = TURNING BREACH — DSM Aurora Imperialis has been overrun. Flesh is spreading through the hull and sections of the station are beginning to disappear. The station remains as an infested ruin. The Imperials are defeated.

faction-station-fall-balreska = TURNING BREACH — NCWL Nova Balreska has been overrun. Flesh is spreading through the foundries and sections of the station are beginning to disappear. The station remains as an infested ruin. The Communards are defeated.

faction-station-fall-tatsumoto = TURNING BREACH — SHI Tatsumoto has been overrun. Aberrant growth is spreading through the shipyard and sections of the station are beginning to disappear. The station remains as an infested ruin. Shinohara is defeated.

faction-station-fall-jackal = TURNING BREACH — GSC Jackal has been overrun. Aberrant flesh is consuming the hull and sections of the station are beginning to disappear. The station remains as an infested ruin. The TFCF is defeated.

faction-station-fall-freeport = TURNING BREACH — The Freeport has been overrun. Flesh is spreading across the docks and sections of the station are beginning to disappear. The station remains as an infested ruin. The TFCF is defeated.

faction-station-fall-gliess = TURNING BREACH — Gliess Santo has been overrun. Aberrant growth is spreading through its streets and sections of the station are beginning to disappear. The station remains as an infested ruin. The CMM is defeated.

faction-station-fall-tribal-hideout = TURNING BREACH — The Tribal Hideout has been overrun. Its old paths are filling with aberrant flesh and parts of the sanctuary are beginning to disappear. The station remains as an infested ruin. The TAP tribes have lost their sanctuary.

faction-station-fall-aasim = TURNING BREACH — Qiwa Aasim has been overrun. Its halls are filling with aberrant flesh and parts of the stronghold are beginning to disappear. The station remains as an infested ruin. The TAP tribes have lost their stronghold.

# Both great powers have fallen. Deliberately names nobody — the remaining forces are still scrapping over it.
faction-victory-minors = The great powers are gone. Their thrones are cold, their fleets are scrap, and there is nobody left to give orders to Taypan. Only the remaining forces are still out there, still fighting over what is left of the sector — and now there is nothing above them to answer to. The age of empires in Taypan is over. Whatever comes next belongs to whoever is still standing when the shooting stops.
