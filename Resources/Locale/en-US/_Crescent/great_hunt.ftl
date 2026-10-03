## Great Altar interaction

great-altar-capture-begin-self = You kneel before the Great Altar and begin the rite. Do not falter.
great-altar-capture-begin-others = { $faction ->
    [TAP] A Pact kinsman kneels at the Great Altar and begins the old rite!
    [SRM] A Militia hunter kneels at the Great Altar and begins to pray!
   *[other] { $faction } is claiming the Great Altar!
}
great-altar-captured = { $faction ->
    [TAP] The altar answers the four families. Green fire wakes upon it.
    [SRM] The altar answers the Saint. Pale blue fire wakes upon it.
   *[other] { $faction } has claimed the Great Altar!
}
great-altar-already-working = You are already deep in the rite.
great-altar-already-yours = The altar already answers to your people.
great-altar-not-allowed = The altar is cold to your touch. It does not answer to you.
great-altar-locked = The hunt is over. The altar has gone silent.
great-altar-grace = The tide still holds the altar dormant — { $seconds } seconds until it can be claimed.

great-altar-examine-unclaimed = [color=gray]The altar sleeps. No one has claimed it yet.[/color]
great-altar-examine-held = { $faction ->
    [TAP] [color=#9FED58]Green fire burns on it. The Pact holds the altar.[/color]
    [SRM] [color=#3A7BFF]Blue fire burns on it. The Saint's Militia holds the altar.[/color]
   *[other] [color=yellow]Held by { $faction }.[/color]
}
great-altar-examine-remaining = [color=orange]{ $time } until the hunt is theirs.[/color]
great-altar-examine-capturing = [color=orange]Someone of { $faction } is performing the rite right now.[/color]
great-altar-examine-grace = [color=gray]The tide still holds the altar dormant — { $seconds } seconds until it can be claimed.[/color]
great-altar-examine-hint = Kneel and hold the rite for { $seconds } seconds to claim it for your people.

## Prayers spoken during the claim rite

great-altar-chant-tap-1 = Great Mother, who bore the first caravans out of the dark, hear your child.
great-altar-chant-tap-2 = You who cradle every route and every family, open your eyes upon this altar.
great-altar-chant-tap-3 = Great Mother, your children have come home. Take this place back into your arms.
great-altar-chant-tap-4 = As you sheltered us in the long night, shelter this altar from the hunters' fire.
great-altar-chant-tap-5 = The Great Mother has woken. This altar is hers, and we are hers.

great-altar-chant-srm-1 = Saint, look upon this defiled altar. I come to purify it.
great-altar-chant-srm-2 = A plague has wrapped itself around the stars. Let it be cleansed from this place.
great-altar-chant-srm-3 = Burn away the rot, burn away the heresy. Let this altar be made clean again.
great-altar-chant-srm-4 = Every star the plague has touched shall be purged. We begin here.
great-altar-chant-srm-5 = It is cleansed. The plague is driven out. This altar belongs to the Saint.

## Great Hunt announcements
## Each side only hears its own copy. $viewer is the side reading it, $faction the side the news is about.

great-hunt-altar-captured = { $viewer ->
    [TAP] { $faction ->
        [TAP] { $previous ->
            [SRM] THE GREAT HUNT — The hunters' hymns fall silent! We have torn the Great Altar from the Militia, and green fire burns where their pale fire burned. Hold it for { $minutes } minutes, children of the Mother, and the hunt is ours.
           *[other] THE GREAT HUNT — Drums sound along the old routes. The four families have claimed the Great Altar. Hold it for { $minutes } minutes and the hunt is ours.
        }
       *[other] { $previous ->
            [TAP] THE GREAT HUNT — The hunters have defiled the Great Altar! Our fire is snuffed out and their hymns rise over the Mother's stone. Take it back before { $minutes } minutes pass, or the old promises break.
           *[other] THE GREAT HUNT — The Saint's Militia has consecrated the Great Altar. Tear it from them before { $minutes } minutes pass, or the hunt is lost.
        }
    }
    [SRM] { $faction ->
        [SRM] { $previous ->
            [TAP] THE GREAT HUNT — The heretics' fire is snuffed out! We have wrested the Great Altar from the Pact and consecrated it anew. Hold it for { $minutes } minutes, faithful, and the Hunt ends in the Saint's name.
           *[other] THE GREAT HUNT — The Overseer's horn sounds. We have consecrated the Great Altar. Hold it for { $minutes } minutes and the Hunt ends in the Saint's name.
        }
       *[other] { $previous ->
            [SRM] THE GREAT HUNT — The heretics have taken the Great Altar from us! Their false Mother's fire burns on the consecrated stone. Purge them before { $minutes } minutes pass, or the Hunt is lost.
           *[other] THE GREAT HUNT — The Pact has claimed the Great Altar. Purge them from it before { $minutes } minutes pass, or the Hunt is lost.
        }
    }
   *[other] THE GREAT HUNT — { $faction } has claimed the Great Altar. Hold it for { $minutes } minutes to win the hunt.
}

great-hunt-altar-warning = { $viewer ->
    [TAP] { $faction ->
        [TAP] THE GREAT HUNT — Green fire still burns on the Great Altar. { $minutes } { $minutes ->
            [one] minute
           *[other] minutes
        } more and the hunt is ours. Hold fast, kin.
       *[other] THE GREAT HUNT — The Militia's hymns still rise over the Great Altar. { $minutes } { $minutes ->
            [one] minute
           *[other] minutes
        } and the old promises break. Take it back!
    }
    [SRM] { $faction ->
        [SRM] THE GREAT HUNT — The Saint's fire still burns on the Great Altar. { $minutes } { $minutes ->
            [one] minute
           *[other] minutes
        } more and the Hunt is won. Hold the line, faithful.
       *[other] THE GREAT HUNT — The heretics still hold the Great Altar. { $minutes } { $minutes ->
            [one] minute
           *[other] minutes
        } and the Hunt is lost. Purge them!
    }
   *[other] THE GREAT HUNT — { $faction } still holds the Great Altar. { $minutes } { $minutes ->
        [one] minute remains
       *[other] minutes remain
    } before the hunt is theirs.
}

great-hunt-victory-tap = { $viewer ->
    [TAP] TAP VICTORY — The Great Altar burns green and answers to the four families. The Militia breaks and scatters into the dark. The routes stay open, the old promises hold, and the Great Hunt is ours.
    [SRM] DEFEAT — The Great Altar burns with the heretics' green fire. The Militia breaks and scatters into the dark. The Great Hunt is lost.
   *[other] TAP VICTORY — The Pact holds the Great Altar. The Great Hunt is over.
}
great-hunt-victory-srm = { $viewer ->
    [SRM] SRM VICTORY — The Great Altar burns blue, consecrated in the Saint's name. The Pact's caravans flee the field and the hymns of the faithful fill the halls. The Great Hunt is won.
    [TAP] DEFEAT — The Great Altar burns with the hunters' pale fire. Our caravans flee the field. The Great Hunt is lost.
   *[other] SRM VICTORY — The Militia holds the Great Altar. The Great Hunt is over.
}
great-hunt-victory-generic = { $viewer ->
    [TAP] { $faction ->
        [TAP] VICTORY — The four families hold the Great Altar. The Great Hunt is ours.
       *[other] DEFEAT — { $faction } holds the Great Altar. The Great Hunt is lost.
    }
    [SRM] { $faction ->
        [SRM] VICTORY — The faithful hold the Great Altar. The Great Hunt is won.
       *[other] DEFEAT — { $faction } holds the Great Altar. The Great Hunt is lost.
    }
   *[other] { $faction } VICTORY — { $faction } holds the Great Altar. The Great Hunt is over.
}

great-hunt-round-end = { $faction ->
    [TAP] The four families hold the Great Altar. The Pact won the Great Hunt.
    [SRM] The Great Altar was consecrated in the Saint's name. The Militia won the Great Hunt.
   *[other] { $faction } won the Great Hunt.
}
great-hunt-summary-winner = { $faction ->
    [TAP] [color=#9FED58]The Taypani-Atyrian Pact held the Great Altar and won the Great Hunt.[/color]
    [SRM] [color=#3A7BFF]The Saint's Militia held the Great Altar and won the Great Hunt.[/color]
   *[other] [color=gold]{ $faction } held the Great Altar and won the Great Hunt.[/color]
}
great-hunt-summary-none = [color=gray]No one held the Great Altar long enough. The altar sleeps, and the Great Hunt ends without a victor.[/color]

## Preparation phase

great-hunt-announcement-sender = The Great Hunt
great-hunt-barrier-name = Tide Shield
great-hunt-grace-start = { $viewer ->
    [TAP] SEVERE TIDE DETECTED — The Mother's veil closes around her children. Emergency shields hold every vessel inside the perimeter, and the Great Altar sleeps beneath the surge. Estimated tide passage time: <{ $time }>... Ready the caravans and the blades. When the tide breaks, the hunters will come for the altar.
    [SRM] SEVERE TIDE DETECTED — Emergency shields hold the faithful and their vessels inside the perimeter, and the defiled Great Altar sleeps beneath the surge. Estimated tide passage time: <{ $time }>... Arm yourselves, Hunters. When the tide breaks, the Hunt begins.
   *[other] SEVERE TIDE DETECTED — Emergency shields raised. All vessels are locked inside the shield perimeter and the Great Altar lies dormant beneath the surge. Estimated tide passage time: <{ $time }>... The hunt begins when the tide breaks.
}
great-hunt-grace-warning = <{ $time }> until the tide passes.
great-hunt-grace-countdown = TIDE PASSING IN <{ $time }>...
great-hunt-grace-over = { $viewer ->
    [TAP] THE TIDE HAS PASSED. The shields are down and the Great Altar has woken in the Hall of Prays. Go, children of the Mother - take back what is ours.
    [SRM] THE TIDE HAS PASSED. The shields are down and the Great Altar has woken in the Hall of Prays. THE HUNT IS ON. Purge it in the Saint's name.
   *[other] THE TIDE HAS PASSED. Emergency shields have been disabled and the Great Altar has woken in the Hall of Prays. THE HUNT IS ON.
}
