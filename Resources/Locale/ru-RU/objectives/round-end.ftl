objectives-round-end-result = {$count ->
    [one] Был один { $agent }.
    *[other] Всего { MAKEPLURAL($agent) }: {$count}.
}
objectives-player-user-named = [color=White]{ $name }[/color] ([color=gray]{ $user }[/color])
objectives-player-named = [color=White]{ $name }[/color]
objectives-no-objectives = { $custody }{ $title } был { $agent }.
objectives-with-objectives = { $custody }{ $title } был { $agent } с целями:
objectives-objective-success = { $objective } | [color={ $markupColor }]Успешно![/color]
objectives-objective-fail = { $objective } | [color={ $markupColor }]Неудачно![/color] ({ $progress }%)
objectives-in-custody = [bold][color=red]| В ЗАКЛЮЧЕНИИ | [/color][/bold]
