reagent-effect-guidebook-explosion-reaction-effect =
    { $chance ->
        [1] Вызывает
        *[other] вызывает
    } взрыв
reagent-effect-guidebook-foam-area-reaction-effect =
    { $chance ->
        [1] Создаёт
        *[other] создаёт
    } большое количество пены
reagent-effect-guidebook-satiate-thirst =
    { $chance ->
        [1] Утоляет
        *[other] утоляет
    } { $relative ->
        [1] жажду со средней скоростью
        *[other] жажду в {NATURALFIXED($relative, 3)} раза быстрее среднего
    }
reagent-effect-guidebook-satiate-hunger =
    { $chance ->
        [1] Утоляет
        *[other] утоляет
    } { $relative ->
        [1] голод со средней скоростью
        *[other] голод в {NATURALFIXED($relative, 3)} раза быстрее среднего
    }
reagent-effect-guidebook-activate-artifact =
    { $chance ->
        [1] Пытается
        *[other] пытается
    } активировать артефакт
reagent-effect-guidebook-set-solution-temperature-effect =
    { $chance ->
        [1] Устанавливает
        *[other] устанавливает
    } температуру раствора ровно на {NATURALFIXED($temperature, 2)} K
reagent-effect-guidebook-adjust-solution-temperature-effect =
    { $chance ->
        [1] { $deltasign ->
                [1] Нагревает
                *[-1] Охлаждает
            }
        *[other]
            { $deltasign ->
                [1] нагревает
                *[-1] охлаждает
            }
    } раствор, пока его температура не { $deltasign ->
                [1] достигнет максимум {NATURALFIXED($maxtemp, 2)} K
                *[-1] опустится до минимум {NATURALFIXED($mintemp, 2)} K
            }
reagent-effect-guidebook-reduce-rotting =
    { $chance ->
        [1] Снимает
        *[other] снимает
    } {NATURALFIXED($time, 3)} сек. гниения
reagent-effect-guidebook-plant-diethylamine =
    { $chance ->
        [1] Повышает
        *[other] повышает
    } продолжительность жизни и/или базовое здоровье растения, с шансом 10% для каждого
reagent-effect-guidebook-plant-robust-harvest =
    { $chance ->
        [1] Повышает
        *[other] повышает
    } потенцию растения на {$increase}, но не выше {$limit}. Когда потенция достигает {$seedlesstreshold}, растение теряет семена. Попытка поднять потенцию выше {$limit} может с шансом 10% снизить урожайность
reagent-effect-guidebook-plant-seeds-add =
    { $chance ->
        [1] Восстанавливает
        *[other] восстанавливает
    } семена растения
reagent-effect-guidebook-plant-seeds-remove =
    { $chance ->
        [1] Удаляет
        *[other] удаляет
    } семена растения
reagent-effect-guidebook-crit-modifier =
    { $deltasign ->
        [-1] Понижает
       *[1] Повышает
    } критический порог на {$amount} ед.
