reagent-effect-condition-guidebook-total-damage =
    { $max ->
        [2147483648] общий урон цели не менее {NATURALFIXED($min, 2)}
        *[other] { $min ->
                    [0] общий урон цели не более {NATURALFIXED($max, 2)}
                    *[other] общий урон цели от {NATURALFIXED($min, 2)} до {NATURALFIXED($max, 2)}
                 }
    }
reagent-effect-condition-guidebook-reagent-threshold =
    { $max ->
        [2147483648] имеется не менее {NATURALFIXED($min, 2)} ед. {$reagent}
        *[other] { $min ->
                    [0] имеется не более {NATURALFIXED($max, 2)} ед. {$reagent}
                    *[other] имеется от {NATURALFIXED($min, 2)} до {NATURALFIXED($max, 2)} ед. {$reagent}
                 }
    }
reagent-effect-condition-guidebook-mob-state-condition = пациент в { $state }
reagent-effect-condition-guidebook-job-condition = должность цели - { $job }
reagent-effect-condition-guidebook-solution-temperature =
    температура раствора { $max ->
            [2147483648] не ниже {NATURALFIXED($min, 2)} K
            *[other] { $min ->
                        [0] не выше {NATURALFIXED($max, 2)} K
                        *[other] от {NATURALFIXED($min, 2)} K до {NATURALFIXED($max, 2)} K
                     }
    }
reagent-effect-condition-guidebook-body-temperature =
    температура тела { $max ->
            [2147483648] не ниже {NATURALFIXED($min, 2)} K
            *[other] { $min ->
                        [0] не выше {NATURALFIXED($max, 2)} K
                        *[other] от {NATURALFIXED($min, 2)} K до {NATURALFIXED($max, 2)} K
                     }
    }
reagent-effect-condition-guidebook-organ-type =
    метаболизирующий орган { $shouldhave ->
                                [true] является
                                *[false] не является
                           } органом типа «{$name}»
reagent-effect-condition-guidebook-has-tag =
    у цели { $invert ->
                 [true] нет
                 *[false] есть
                } тега {$tag}
