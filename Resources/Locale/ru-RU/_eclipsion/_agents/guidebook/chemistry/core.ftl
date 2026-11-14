guidebook-reagent-effect-description =
    {$chance ->
        [1] { $effect }
        *[other] С вероятностью { NATURALPERCENT($chance, 2) } { $effect }
    }{ $conditionCount ->
        [0] .
        *[other] {" "}если { $conditions }.
    }
