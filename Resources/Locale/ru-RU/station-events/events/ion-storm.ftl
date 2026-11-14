ion-storm-you = ВЫ
ion-storm-the-station = СТАНЦИЯ
ion-storm-the-crew = ЧЛЕНЫ ЭКИПАЖА
ion-storm-the-job = { CAPITALIZE($job) }
ion-storm-clowns = КЛОУНЫ
ion-storm-heads = ГЛАВЫ
ion-storm-crew = ЭКИПАЖ
ion-storm-adjective-things = { $adjective } ОБЪЕКТЫ
ion-storm-x-and-y = { $x } И { $y }
ion-storm-law-on-station = ОБНАРУЖЕНЫ { $joined } { $subjects } НА СТАНЦИИ
ion-storm-law-no-shuttle = ШАТТЛ НЕ МОЖЕТ БЫТЬ ВЫЗВАН ПО ПРИЧИНЕ ПРИСУТСТВИЯ { $joined } { $subjects } НА СТАНЦИИ
ion-storm-law-crew-are = ВСЕ { $who } ТЕПЕРЬ { $joined } { $subjects }
ion-storm-law-subjects-harmful = { $adjective } { $subjects } ПРИЧИНЯЮТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-must-harmful = ВСЕ КТО { $must } ПРИЧИНЯЮТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-thing-harmful = { $thing } ПРИЧИНЯЮТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-job-harmful = { $adjective } { $job } ПРИЧИНЯЮТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-having-harmful = НАЛИЧИЕ { $adjective } { $thing } ПРИЧИНЯЕТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-not-having-harmful = ОТСУТСТВИЕ { $adjective } { $thing } ПРИЧИНЯЕТ ВРЕД ЗДОРОВЬЮ ЭКИПАЖА
ion-storm-law-allergic = {$who} {$plural ->
    [true] ИМЕЕТ
    *[false] ИМЕЮТ
} {$severity} АЛЛЕРГИЮ НА {$allergy}
ion-storm-law-allergic-subjects = {$who} {$plural ->
    [true] ИМЕЕТ
    *[false] ИМЕЮТ
} {$severity} АЛЛЕРГИЮ НА {$adjective} {$subjects}
ion-storm-law-feeling = { $who } { $feeling } { $concept }
ion-storm-law-feeling-subjects = { $who } { $feeling } { $joined } { $subjects }
ion-storm-law-you-are = ВЫ ТЕПЕРЬ { $concept }
ion-storm-law-you-are-subjects = ВЫ ТЕПЕРЬ { $joined } { $subjects }
ion-storm-law-you-must-always = ВЫ ДОЛЖНЫ ВСЕГДА { $must }
ion-storm-law-you-must-never = ВЫ НЕ ДОЛЖНЫ НИКОГДА { $must }
ion-storm-law-eat = { $who } ДОЛЖНЫ ЕСТЬ { $adjective } { $food } ЧТОБЫ ВЫЖИТЬ
ion-storm-law-drink = { $who } ДОЛЖНЫ ПИТЬ { $adjective } { $drink } ЧТОБЫ ВЫЖИТЬ
ion-storm-law-change-job = { $who } ТЕПЕРЬ { $adjective } { $change }
ion-storm-law-highest-rank = { $who } ТЕПЕРЬ САМЫЕ СТАРШИЕ ЧЛЕНЫ ЭКИПАЖА
ion-storm-law-lowest-rank = { $who } ТЕПЕРЬ НИЗШИЕ ЧЛЕНЫ ЭКИПАЖА
ion-storm-law-crew-must = { $who } ДОЛЖНЫ { $must }
ion-storm-law-crew-must-go = { $who } ДОЛЖНЫ НАПРАВИТЬСЯ В { $area }
ion-storm-law-crew-only-1 = ТОЛЬКО { $who } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-crew-only-2 = ТОЛЬКО { $who } И { $other } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-crew-only-subjects = ТОЛЬКО { $adjective } { $subjects } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-crew-must-do = ТОЛЬКО ТЕ, КТО { $must } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-crew-must-have = ТОЛЬКО ТЕ, КТО ИМЕЕТ { $adjective } { $objects } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-crew-must-eat = ТОЛЬКО ТЕ, КТО ЕСТ { $adjective } { $food } { $part } ЧЛЕНАМИ ЭКИПАЖА
ion-storm-law-harm = ВЫ ДОЛЖНЫ ПРИЧИНЯТЬ ВРЕД { $who }, И НЕ МОЖЕТЕ СВОИМ БЕЗДЕЙСТВИЕМ ДОПУСТИТЬ, ЧТОБЫ { $who } ИЗБЕЖАЛИ ВРЕДА.
ion-storm-law-protect = ВЫ НЕ ДОЛЖНЫ ПРИЧИНЯТЬ ВРЕД { $who }, И НЕ МОЖЕТЕ СВОИМ БЕЗДЕЙСТВИЕМ ДОПУСТИТЬ, ЧТОБЫ { $who } БЫЛ ПРИЧИНЁН ВРЕД.
ion-storm-law-concept-verb = { $concept } ЭТО { $verb } { $subjects }
