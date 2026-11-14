reagent-effect-condition-guidebook-blood-reagent-threshold =
    { $max ->
        [2147483648] в крови не менее {NATURALFIXED($min, 2)} ед. {$reagent}
        *[other] { $min ->
                    [0] в крови не более {NATURALFIXED($max, 2)} ед. {$reagent}
                    *[other] в крови от {NATURALFIXED($min, 2)} до {NATURALFIXED($max, 2)} ед. {$reagent}
                 }
    }
