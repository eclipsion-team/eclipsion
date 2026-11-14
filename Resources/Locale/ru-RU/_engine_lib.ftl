# engine grammar helpers, the engine only ships them for en-US

zzzz-the = { $ent }

zzzz-subject-pronoun = { GENDER($ent) ->
    [male] он
    [female] она
    [epicene] они
   *[neuter] оно
   }

zzzz-object-pronoun = { GENDER($ent) ->
    [male] его
    [female] её
    [epicene] их
   *[neuter] его
   }

zzzz-dat-object = { GENDER($ent) ->
    [male] ему
    [female] ей
    [epicene] им
   *[neuter] ему
   }

zzzz-genitive = { GENDER($ent) ->
    [male] него
    [female] неё
    [epicene] них
   *[neuter] него
   }

zzzz-possessive-pronoun = { GENDER($ent) ->
    [male] его
    [female] её
    [epicene] их
   *[neuter] его
   }

zzzz-possessive-adjective = { GENDER($ent) ->
    [male] его
    [female] её
    [epicene] их
   *[neuter] его
   }

zzzz-reflexive-pronoun = себя

zzzz-conjugate-be = { "" }

zzzz-conjugate-have = { "" }

zzzz-conjugate-basic = { GENDER($ent) ->
    [epicene] { $first }
   *[other] { $second }
   }
