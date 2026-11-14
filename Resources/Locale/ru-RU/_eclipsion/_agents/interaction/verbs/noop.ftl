interaction-LookAt-success-self-popup = Вы смотрите на {THE($target)}.
interaction-LookAt-success-target-popup = Вы чувствуете, что {THE($user)} смотрит на вас...
interaction-LookAt-success-others-popup = {THE($user)} смотрит на {THE($target)}.
interaction-Hug-success-self-popup = Вы обнимаете {THE($target)}.
interaction-Hug-success-target-popup = {THE($user)} обнимает вас.
interaction-Hug-success-others-popup = {THE($user)} обнимает {THE($target)}.
interaction-Pet-success-self-popup = Вы гладите {THE($target)} по голове.
interaction-Pet-success-target-popup = {THE($user)} гладит вас по голове.
interaction-Pet-success-others-popup = {THE($user)} гладит {THE($target)}.
interaction-PetAnimal-description = Погладить животное.
interaction-PetAnimal-success-self-popup = {interaction-Pet-success-self-popup}
interaction-PetAnimal-success-target-popup = {interaction-Pet-success-target-popup}
interaction-PetAnimal-success-others-popup = {interaction-Pet-success-others-popup}
interaction-KnockOn-success-self-popup = Вы стучите по {THE($target)}.
interaction-KnockOn-success-target-popup = {THE($user)} стучит по вам.
interaction-KnockOn-success-others-popup = {THE($user)} стучит по {THE($target)}.
interaction-Rattle-success-self-popup = Вы трясёте {THE($target)}.
interaction-Rattle-success-target-popup = {THE($user)} трясёт вас.
interaction-Rattle-success-others-popup = {THE($user)} трясёт {THE($target)}.
interaction-WaveAt-success-self-popup = Вы машете {$hasUsed ->
    [false] рукой {THE($target)}.
    *[true] {THE($target)}, в руке — {$used}.
}
interaction-WaveAt-success-target-popup = {THE($user)} машет {$hasUsed ->
    [false] вам рукой.
    *[true] вам, в руке — {$used}.
}
interaction-WaveAt-success-others-popup = {THE($user)} машет {$hasUsed ->
    [false] рукой {THE($target)}.
    *[true] {THE($target)}, в руке — {$used}.
}
