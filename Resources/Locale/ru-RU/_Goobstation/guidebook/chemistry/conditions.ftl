reagent-effect-condition-guidebook-stamina-damage-threshold =
    { $max ->
        [2147483648] у цели как минимум {NATURALFIXED($min, 2)} урона выносливости
        *[other] { $min ->
                    [0] у цели не более {NATURALFIXED($max, 2)} урона выносливости
                    *[other] у цели от {NATURALFIXED($min, 2)} до {NATURALFIXED($max, 2)} урона выносливости
                 }
    }
reagent-effect-condition-guidebook-unique-bloodstream-chem-threshold =
    { $max ->
        [2147483648] { $min ->
                        [1] в крови есть как минимум {$min} реагент
                        *[other] в крови есть как минимум {$min} реагентов
                     }
        [1] { $min ->
               [0] в крови не более {$max} реагента
               *[other] в крови от {$min} до {$max} реагентов
            }
        *[other] { $min ->
                    [-1] в крови не более {$max} реагентов
                    *[other] в крови от {$min} до {$max} реагентов
                 }
    }
reagent-effect-condition-guidebook-typed-damage-threshold =
    { $inverse ->
        [true] у цели не более
        *[false] у цели как минимум
    } { $changes } урона
