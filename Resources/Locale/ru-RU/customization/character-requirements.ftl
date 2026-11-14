character-job-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны занимать одну из этих должностей: {$jobs}
character-department-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны состоять в одном из этих отделов: {$departments}
character-timer-department-insufficient = Вам необходимо наиграть еще [color=yellow]{ TOSTRING($time, "0") }[/color] минут в [color={ $departmentColor }]{ $department }[/color] отделе
character-timer-department-too-high = Вам необходимо иметь на [color=yellow]{ TOSTRING($time, "0") }[/color] меньше минут в [color={ $departmentColor }]{ $department }[/color] отделе
character-timer-overall-insufficient = Вам необходимо наиграть еще [color=yellow]{ TOSTRING($time, "0") }[/color] минут
character-timer-overall-too-high = Вам необходимо иметь на [color=yellow]{ TOSTRING($time, "0") }[/color] меньше минут в игре
character-timer-role-insufficient = Вам необходимо наиграть еще [color=yellow]{ TOSTRING($time, "0") }[/color] минут на роли [color={ $departmentColor }]{ $job }[/color]
character-timer-role-too-high = Вам необходимо иметь на [color=yellow]{ TOSTRING($time, "0") }[/color] меньше минут на роли [color={ $departmentColor }]{ $job }[/color]
character-logic-and-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны соответствовать [color=red]всем[/color] [color=gray]этим[/color] условиям: {$options}
character-logic-or-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны соответствовать [color=red]хотя бы одному[/color] из [color=white]этих[/color] условий: {$options}
character-logic-xor-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны соответствовать [color=red]только одному[/color] из [color=white]этих[/color] условий: {$options}
character-age-requirement-range = Ваш возраст{$inverted ->
    [true]{" "}не
    *[other]{""}
} должен быть от [color=yellow]{$min}[/color] до [color=yellow]{$max}[/color] лет
character-age-requirement-minimum-only = Вам должно быть {$inverted ->
    [true] меньше
    *[other] не меньше
} [color=yellow]{$min}[/color] лет
character-age-requirement-maximum-only = Вам должно быть {$inverted ->
    [true] больше
    *[other] не больше
} [color=yellow]{$max}[/color] лет
character-backpack-type-requirement = Вы {$inverted ->
    [true] не должны использовать
    *[other] должны использовать
} [color=brown]{$type}[/color] в качестве сумки
character-clothing-preference-requirement = Вы {$inverted ->
    [true] не должны носить
    *[other] должны носить
} [color=white]{$type}[/color]
character-gender-requirement = У вас {$inverted ->
    [true] не должно быть
    *[other] должны быть
} местоимения [color=white]{$gender}[/color]
character-sex-requirement = Ваш пол{$inverted ->
    [true]{" "}не
    *[other]{""}
} должен быть [color=white]{$sex ->
    [None] «без пола»
    *[other] {$sex}
}[/color]
character-species-requirement = Ваш вид{$inverted ->
    [true]{" "}не
    *[other]{""}
} должен быть: {$species}
character-height-requirement = Ваш рост{$inverted ->
    [true]{" "}не
    *[other]{""}
} должен быть {$min ->
    [-2147483648]{$max ->
        [2147483648]{""}
        *[other] меньше [color={$color}]{$max}[/color] см
    }
    *[other]{$max ->
        [2147483648] больше [color={$color}]{$min}[/color] см
        *[other] от [color={$color}]{$min}[/color] до [color={$color}]{$max}[/color] см
    }
}
character-width-requirement = Ваша ширина{$inverted ->
    [true]{" "}не
    *[other]{""}
} должна быть {$min ->
    [-2147483648]{$max ->
        [2147483648]{""}
        *[other] меньше [color={$color}]{$max}[/color] см
    }
    *[other]{$max ->
        [2147483648] больше [color={$color}]{$min}[/color] см
        *[other] от [color={$color}]{$min}[/color] до [color={$color}]{$max}[/color] см
    }
}
character-weight-requirement = Ваш вес{$inverted ->
    [true]{" "}не
    *[other]{""}
} должен быть {$min ->
    [-2147483648]{$max ->
        [2147483648]{""}
        *[other] меньше [color={$color}]{$max}[/color] кг
    }
    *[other]{$max ->
        [2147483648] больше [color={$color}]{$min}[/color] кг
        *[other] от [color={$color}]{$min}[/color] до [color={$color}]{$max}[/color] кг
    }
}
character-trait-requirement = У вас {$inverted ->
    [true] не должно быть ни одной
    *[other] должна быть одна
} из этих черт: {$traits}
character-loadout-requirement = У вас {$inverted ->
    [true] не должно быть ни одного
    *[other] должен быть один
} из этих предметов снаряжения: {$loadouts}
character-item-group-requirement = У вас должно быть {$inverted ->
    [true] {$max} или больше
    *[other] {$max} или меньше
} предметов из группы [color=white]{$group}[/color]
character-whitelist-requirement = Вы{$inverted ->
    [true]{" "}не
    *[other]{""}
} должны быть в вайтлисте
