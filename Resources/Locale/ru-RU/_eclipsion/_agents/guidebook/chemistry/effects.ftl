reagent-effect-guidebook-emp-reaction-effect =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } электромагнитный импульс
reagent-effect-guidebook-smoke-area-reaction-effect =
    { $chance ->
        [1] Создаёт
        *[other] создаёт
    } большое количество дыма
reagent-effect-guidebook-status-effect =
    { $type ->
        [add]   { $chance ->
                    [1] Вызывает
                    *[other] вызывает
                } {LOC($key)} минимум на {NATURALFIXED($time, 3)} {MANY("second", $time)} с накоплением
        *[set]  { $chance ->
                    [1] Вызывает
                    *[other] вызывает
                } {LOC($key)} минимум на {NATURALFIXED($time, 3)} {MANY("second", $time)} без накопления
        [remove]{ $chance ->
                    [1] Снимает
                    *[other] снимает
                } {NATURALFIXED($time, 3)} {MANY("second", $time)} эффекта «{LOC($key)}»
    }
reagent-effect-guidebook-adjust-reagent-reagent =
    { $chance ->
        [1] { $deltasign ->
                [1] Добавляет
                *[-1] Убирает
            }
        *[other]
            { $deltasign ->
                [1] добавляет
                *[-1] убирает
            }
    } {NATURALFIXED($amount, 2)} ед. {$reagent} { $deltasign ->
        [1] в раствор
        *[-1] из раствора
    }
reagent-effect-guidebook-adjust-reagent-group =
    { $chance ->
        [1] { $deltasign ->
                [1] Добавляет
                *[-1] Убирает
            }
        *[other]
            { $deltasign ->
                [1] добавляет
                *[-1] убирает
            }
    } {NATURALFIXED($amount, 2)} ед. реагентов группы {$group} { $deltasign ->
            [1] в раствор
            *[-1] из раствора
        }
reagent-effect-guidebook-adjust-temperature =
    { $chance ->
        [1] { $deltasign ->
                [1] Добавляет
                *[-1] Отнимает
            }
        *[other]
            { $deltasign ->
                [1] добавляет
                *[-1] отнимает
            }
    } {POWERJOULES($amount)} тепла { $deltasign ->
            [1] телу, в котором находится
            *[-1] у тела, в котором находится
        }
reagent-effect-guidebook-chem-cause-disease =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } болезнь { $disease }
reagent-effect-guidebook-chem-cause-random-disease =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } болезни { $diseases }
reagent-effect-guidebook-jittering =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } дрожь
reagent-effect-guidebook-chem-clean-bloodstream =
    { $chance ->
        [1] Очищает
        *[other] очищает
    } кровь от других химикатов
reagent-effect-guidebook-cure-disease =
    { $chance ->
        [1] Излечивает
        *[other] излечивает
    } болезни
reagent-effect-guidebook-cure-eye-damage =
    { $chance ->
        [1] { $deltasign ->
                [1] Повреждает
                *[-1] Лечит
            }
        *[other]
            { $deltasign ->
                [1] повреждает
                *[-1] лечит
            }
    } глаза
reagent-effect-guidebook-chem-vomit =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } рвоту
reagent-effect-guidebook-create-gas =
    { $chance ->
        [1] Создаёт
        *[other] создаёт
    } { $moles } { $moles ->
        [1] моль
        *[other] моль
    } газа { $gas }
reagent-effect-guidebook-drunk =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } опьянение
reagent-effect-guidebook-electrocute =
    { $chance ->
        [1] Бьёт током
        *[other] бьёт током
    } усвоившего на {NATURALFIXED($time, 3)} {MANY("second", $time)}
reagent-effect-guidebook-extinguish-reaction =
    { $chance ->
        [1] Тушит
        *[other] тушит
    } огонь
reagent-effect-guidebook-flammable-reaction =
    { $chance ->
        [1] Повышает
        *[other] повышает
    } воспламеняемость
reagent-effect-guidebook-ignite =
    { $chance ->
        [1] Поджигает
        *[other] поджигает
    } усвоившего
reagent-effect-guidebook-make-sentient =
    { $chance ->
        [1] Делает
        *[other] делает
    } усвоившего разумным
reagent-effect-guidebook-make-polymorph =
    { $chance ->
        [1] Превращает
        *[other] превращает
    } усвоившего в { $entityname }
reagent-effect-guidebook-modify-bleed-amount =
    { $chance ->
        [1] { $deltasign ->
                [1] Усиливает
                *[-1] Ослабляет
            }
        *[other] { $deltasign ->
                    [1] усиливает
                    *[-1] ослабляет
                 }
    } кровотечение
reagent-effect-guidebook-modify-blood-level =
    { $chance ->
        [1] { $deltasign ->
                [1] Повышает
                *[-1] Понижает
            }
        *[other] { $deltasign ->
                    [1] повышает
                    *[-1] понижает
                 }
    } уровень крови
reagent-effect-guidebook-paralyze =
    { $chance ->
        [1] Парализует
        *[other] парализует
    } усвоившего минимум на {NATURALFIXED($time, 3)} {MANY("second", $time)}
reagent-effect-guidebook-movespeed-modifier =
    { $chance ->
        [1] Изменяет
        *[other] изменяет
    } скорость передвижения в {NATURALFIXED($walkspeed, 3)}x минимум на {NATURALFIXED($time, 3)} {MANY("second", $time)}
reagent-effect-guidebook-reset-narcolepsy =
    { $chance ->
        [1] Временно подавляет
        *[other] временно подавляет
    } нарколепсию
reagent-effect-guidebook-wash-cream-pie-reaction =
    { $chance ->
        [1] Смывает
        *[other] смывает
    } кремовый торт с лица
reagent-effect-guidebook-cure-zombie-infection =
    { $chance ->
        [1] Излечивает
        *[other] излечивает
    } текущую зомби-инфекцию
reagent-effect-guidebook-cause-zombie-infection =
    { $chance ->
        [1] Заражает
        *[other] заражает
    } существо зомби-инфекцией
reagent-effect-guidebook-innoculate-zombie-infection =
    { $chance ->
        [1] Излечивает
        *[other] излечивает
    } текущую зомби-инфекцию и даёт иммунитет к будущим заражениям
reagent-effect-guidebook-missing =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } неизвестный эффект, так как его описание ещё никто не написал
reagent-effect-guidebook-change-glimmer-reaction-effect =
    { $chance ->
        [1] Изменяет
        *[other] изменяет
    } уровень мерцания на {$count} ед.
reagent-effect-guidebook-chem-remove-psionic =
    { $chance ->
        [1] Отнимает
        *[other] отнимает
    } псионические способности
reagent-effect-guidebook-chem-reroll-psionic =
    { $chance ->
        [1] Даёт
        *[other] даёт
    } шанс получить другую псионическую способность
reagent-effect-guidebook-add-moodlet =
    Изменяет настроение на {$amount}
    { $timeout ->
        [0] бессрочно
        *[other] на {$timeout} сек.
    }
reagent-effect-guidebook-remove-moodlet =
    Снимает эффект настроения «{$name}».
reagent-effect-guidebook-purge-moodlets =
    Снимает все активные непостоянные эффекты настроения.
reagent-effect-guidebook-stamina-change =
    { $chance ->
        [1] { $deltasign ->
                [-1] Повышает
                *[1] Понижает
            }
        *[other] { $deltasign ->
                    [-1] повышает
                    *[1] понижает
                 }
    } выносливость на {$amount} ед.
reagent-effect-guidebook-add-to-chemicals =
    { $chance ->
        [1] { $deltasign ->
                [1] Добавляет
                *[-1] Убирает
            }
        *[other]
            { $deltasign ->
                [1] добавляет
                *[-1] убирает
            }
    } {NATURALFIXED($amount, 2)} ед. {$reagent} { $deltasign ->
        [1] в раствор
        *[-1] из раствора
    }
reagent-effect-guidebook-chem-restorereroll-psionic =
    { $chance ->
        [1] Восстанавливает
        *[other] восстанавливает
    } способность получать пользу от реагентов, раскрывающих разум
reagent-effect-guidebook-add-to-solution-reaction =
    { $chance ->
        [1] Добавляет
        *[other] добавляет
    } полученный реагент в раствор
reagent-effect-guidebook-area-reaction =
    { $chance ->
        [1] Создаёт
        *[other] создаёт
    } облако дыма или пены на {NATURALFIXED($duration, 3)} {MANY("second", $duration)}
reagent-effect-guidebook-deal-stamina-damage =
    { $chance ->
        [1] { $deltasign ->
                [1] Наносит
                *[-1] Восстанавливает
             }
        *[other] { $deltasign ->
                [1] наносит
                *[-1] восстанавливает
             }
    } {NATURALFIXED($amount, 2)} ед. урона выносливости{ $immediate ->
        [true] {" "}мгновенно
        *[false] {""}
    }
reagent-effect-guidebook-plant-attribute =
    { $chance ->
        [1] Изменяет
        *[other] изменяет
    } [color={$colorName}]{$attribute}[/color] растения на {$amount}
reagent-effect-guidebook-plant-cryoxadone =
    { $chance ->
        [1] Омолаживает
        *[other] омолаживает
    } растение, уменьшая его возраст
reagent-effect-guidebook-plant-phalanximine =
    { $chance ->
        [1] Восстанавливает
        *[other] восстанавливает
    } всхожесть семян мёртвого растения
