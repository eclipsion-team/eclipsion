## Saint's Militia psion hunting (PsionHunterSystem)

psion-hunter-psionics-stripped = The Militia's discipline leaves no room for the noosphere in you. Whatever stirred there is gone; you hunt psions, you are not one.

psion-hunter-sense-none = You still yourself and feel for it. No taint of the noosphere hangs in the air.
psion-hunter-sense-found = { $count ->
        [one] The taint of a psion hangs in the air, strongest {$direction}.
       *[other] The taint of {$count} psions hangs in the air, strongest {$direction}.
    }
psion-hunter-sense-close = The closest is near enough to touch.

psion-hunter-direction-here = right on top of you
psion-hunter-direction-north = to the north
psion-hunter-direction-northeast = to the northeast
psion-hunter-direction-east = to the east
psion-hunter-direction-southeast = to the southeast
psion-hunter-direction-south = to the south
psion-hunter-direction-southwest = to the southwest
psion-hunter-direction-west = to the west
psion-hunter-direction-northwest = to the northwest
psion-hunter-direction-invalid = somewhere near

psion-hunter-scrutiny-start = You study {THE($target)} closely...
psion-hunter-scrutiny-psion = {CAPITALIZE(THE($target))} reeks of the noosphere. A psion.
psion-hunter-scrutiny-mindbroken = The noosphere has burnt out of {THE($target)}. Whatever {SUBJECT($target)} {CONJUGATE-BE($target)}, {SUBJECT($target)} {CONJUGATE-BE($target)} no psion now.
psion-hunter-scrutiny-clean = There is no taint of the noosphere on {THE($target)}.

psion-hunter-ward-start = You set your will against the noosphere. Nothing psionic lives near you now.
psion-hunter-ward-start-others = {CAPITALIZE(THE($user))} goes still, and the air around {OBJECT($user)} turns dead and heavy.
psion-hunter-ward-already = A null field already surrounds you.
psion-hunter-ward-end = Your ward lapses. The noosphere creeps back in.
