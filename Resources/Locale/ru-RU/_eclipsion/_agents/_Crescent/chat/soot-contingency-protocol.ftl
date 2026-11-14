soot-protocol-activated = АКТИВИРОВАН АВАРИЙНЫЙ ПРОТОКОЛ «СУТ». Вам больше недоступны OOC, LOOC, чат мёртвых, радио, сообщения КПК и любые записи. Разрешены только эмодзи и эмоции, а всё, что вы попробуете сказать вслух, прозвучит куда приятнее. Длительность: {$duration}. Причина: {$reason}
soot-protocol-deactivated = Аварийный протокол «Сут» снят. Вы снова можете общаться.
soot-protocol-expired = Аварийный протокол «Сут» истёк. Вы снова можете общаться.
soot-protocol-blocked = Действует аварийный протокол «Сут»: можно отправлять только эмодзи и эмоции.
soot-protocol-blocked-writing = Действует аварийный протокол «Сут»: писать можно только эмодзи.
soot-protocol-phrase-1 = Я тебя люблю.
soot-protocol-phrase-2 = Мне нравится твоя шляпа.
soot-protocol-phrase-3 = Привет, меня зовут Сут. Я хороший человек.
soot-protocol-phrase-4 = Ты сегодня чудесно выглядишь!
soot-protocol-phrase-5 = По-моему, все здесь отлично справляются.
soot-protocol-phrase-6 = Тебе кто-нибудь говорил, что ты просто чудо?
soot-protocol-phrase-7 = Хочу крепко обнять всех-всех.
soot-protocol-phrase-8 = Какой чудесный сегодня день!
soot-protocol-phrase-9 = Я ценю тебя, друг.
soot-protocol-phrase-10 = У тебя очень красивая обувь.
soot-protocol-phrase-11 = Обещаю всегда вести себя мило и вежливо.
soot-protocol-phrase-12 = Я очень люблю админов.
soot-protocol-phrase-13 = Спасибо, что ты мой друг.
soot-protocol-phrase-14 = У тебя красивая улыбка.
soot-protocol-phrase-15 = Я просто хочу, чтобы все были счастливы.
soot-protocol-phrase-16 = Привет! Я Сут, и я обожаю щенков.
soot-protocol-phrase-17 = Делиться — значит заботиться!
soot-protocol-phrase-18 = У меня для всех печенье. Оно сделано из любви.
soot-protocol-phrase-19 = Вы все мои лучшие друзья.
soot-protocol-phrase-20 = Мне нравится твоя стрижка, она тебе очень идёт.
soot-protocol-phrase-21 = Хорошей всем смены!
soot-protocol-phrase-22 = Я в тебя верю!
soot-protocol-phrase-23 = От радуги мне так радостно.
soot-protocol-phrase-24 = Можно меня обнять, пожалуйста?
soot-protocol-phrase-25 = Как же здорово быть здесь со всеми вами.
soot-protocol-fun-1 = Спасибо!
soot-protocol-fun-2 = Да, конечно.
soot-protocol-fun-3 = Несите любовь.
soot-protocol-fun-4 = Огромное спасибо.
soot-protocol-fun-5 = Конечно, друг!
soot-protocol-fun-6 = Несите любовь, а не ненависть.
soot-protocol-fun-7 = Да! Безусловно!
soot-protocol-fun-8 = Спасибо, админы.
soot-protocol-fun-9 = Да, конечно, всё что угодно.
soot-protocol-fun-10 = Мира и любви всем.
soot-protocol-fun-11 = Несите любовь!
soot-protocol-fun-12 = Спасибо за вашу службу.
soot-protocol-fun-13 = Да, конечно!
soot-protocol-fun-14 = Обнимаю всех!
soot-protocol-fun-15 = Спасибо, спасибо, спасибо.
soot-protocol-fun-16 = Будьте добрее. Несите любовь.
cmd-sootcontingencyprotocol-desc = Закрывает игроку все каналы чата (OOC, LOOC, чат мёртвых, радио, КПК) и возможность писать, оставляя только эмодзи и эмоции. Всё, что игрок попытается сказать вслух, превращается в приятную фразу.
cmd-sootcontingencyprotocol-help = Использование: sootcontingencyprotocol activate <игрок> [минуты, 0 = до отключения] [причина...]
    sootcontingencyprotocol deactivate <игрок>
    sootcontingencyprotocol status <игрок>
    sootcontingencyprotocol list
    sootcontingencyprotocol adminfun <игрок> [on|off]
cmd-sootcontingencyprotocol-player-not-found = Не удалось найти игрока {$player}.
cmd-sootcontingencyprotocol-no-reason = не указана
cmd-sootcontingencyprotocol-permanent = до отключения
cmd-sootcontingencyprotocol-activated = Аварийный протокол «Сут» включён для {$player} ({$duration}).
cmd-sootcontingencyprotocol-deactivated = Аварийный протокол «Сут» отключён для {$player}.
cmd-sootcontingencyprotocol-not-active = На {$player} не действует аварийный протокол «Сут».
cmd-sootcontingencyprotocol-admin-activated = {$admin} включает аварийный протокол «Сут» для {$player} ({$duration}).
cmd-sootcontingencyprotocol-admin-deactivated = {$admin} отключает аварийный протокол «Сут» для {$player}.
cmd-sootcontingencyprotocol-list-empty = Аварийный протокол «Сут» ни на кого не действует.
cmd-sootcontingencyprotocol-list-header = Игроков под аварийным протоколом «Сут»: {$count}
cmd-sootcontingencyprotocol-entry = {$player} — админ: {$admin}, осталось: {$remaining}, причина: {$reason}{$adminfun ->
    [true] , adminfun вкл.
    *[other] {""}
}
cmd-sootcontingencyprotocol-adminfun = Adminfun аварийного протокола «Сут» для {$player}: {$state}.
cmd-sootcontingencyprotocol-admin-adminfun = {$admin} переключает adminfun аварийного протокола «Сут» для {$player}: {$state}.
cmd-sootcontingencyprotocol-adminfun-not-active = На {$player} не действует аварийный протокол «Сут». Сначала включите его.
cmd-sootcontingencyprotocol-hint-activate = Заглушить игрока, только эмодзи
cmd-sootcontingencyprotocol-hint-deactivate = Снять заглушку
cmd-sootcontingencyprotocol-hint-status = Показать, заглушён ли игрок
cmd-sootcontingencyprotocol-hint-list = Список всех заглушённых игроков
cmd-sootcontingencyprotocol-hint-adminfun = Заглушённый игрок время от времени сам говорит приятные вещи
cmd-sootcontingencyprotocol-hint-adminfun-state = { "[on|off, пусто — переключить]" }
cmd-sootcontingencyprotocol-hint-minutes = { "[минуты, 0 = до отключения]" }
cmd-sootcontingencyprotocol-hint-reason = { "[причина]" }
