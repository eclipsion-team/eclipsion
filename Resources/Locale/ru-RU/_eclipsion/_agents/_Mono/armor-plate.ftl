armor-plate-break = Ваша пластина «{$plateName}» разрушена!
armor-plate-examine-with-plate = Установлена пластина: [color=yellow]{$plateName}[/color]. Прочность: [color={$durabilityColor}]{$percent}%[/color]
armor-plate-examine-with-plate-simple = Установлена пластина: [color=yellow]{$plateName}[/color].
armor-plate-examine-no-plate = Бронепластина не установлена.
armor-plate-examine-no-storage = Нет отсека для бронепластин.
armor-plate-examinable-verb-text = Свойства пластины
armor-plate-examinable-verb-message = Изучить защиту и прочность.
armor-plate-attributes-examine = Эта бронепластина:
armor-plate-initial-durability = Рассчитана на [color=yellow]{ $durability }[/color] стандартных единиц урона.
armor-plate-speed-display =
    { $deltasign ->
        [-1] Увеличивает {$gait} на [color=yellow]{$speedPercent}%[/color].
         [0] Не влияет на скорость.
         [1] Снижает {$gait} на [color=yellow]{$speedPercent}%[/color].
        *[other] Такого значения скорости быть не должно!
    }
armor-plate-ratios-display =
    { $deltasign ->
        [-1] [color=cyan]Поглощает[/color] [color=yellow]{$ratioPercent}%[/color] урона типа [color=yellow]{$dmgType}[/color] и получает его как [color=yellow]x{$multiplier}[/color] урона прочности.
         [0] Не реагирует на {$dmgType}
         [1] [color=fuchsia]Усиливает[/color] урон типа [color=yellow]{$dmgType}[/color] на [color=yellow]{$ratioPercent}%[/color] и получает добавленный урон как [color=yellow]x{$multiplier}[/color] урона прочности.
        *[other] Такого значения поглощения для {$dmgType} быть не должно!
    }
armor-plate-stamina-value = Наносит [color=yellow]{$multiplier}%[/color] поглощённого урона как урон выносливости.
