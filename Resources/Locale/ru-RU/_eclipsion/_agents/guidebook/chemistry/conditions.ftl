reagent-effect-condition-guidebook-total-hunger =
    { $max ->
        [2147483648] общий голод цели не менее {NATURALFIXED($min, 2)}
        *[other] { $min ->
                    [0] общий голод цели не более {NATURALFIXED($max, 2)}
                    *[other] общий голод цели от {NATURALFIXED($min, 2)} до {NATURALFIXED($max, 2)}
                 }
    }
reagent-effect-condition-guidebook-this-reagent = этого реагента
