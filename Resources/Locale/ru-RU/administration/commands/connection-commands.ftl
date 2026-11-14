cmd-grant_connect_bypass-desc = Временно разрешить пользователю игнорировать обычные проверки подключения.
cmd-grant_connect_bypass-help = Использование: grant_connect_bypass <user> [duration minutes]
    Временно позволяет пользователю обходить обычные ограничения на подключение.
    Обход действует только на этом игровом сервере и истекает (по умолчанию) через 1 час.
    Пользователь сможет зайти независимо от вайтлиста, бункера и лимита игроков.
cmd-grant_connect_bypass-arg-user = <пользователь>
cmd-grant_connect_bypass-arg-duration = [продолжительность в минутах]
cmd-grant_connect_bypass-invalid-args = Ожидалось 1 или 2 аргумента
cmd-grant_connect_bypass-unknown-user = Невозможно найти пользователя '{ $user }'
cmd-grant_connect_bypass-invalid-duration = Неверная продолжительность '{ $duration }'
cmd-grant_connect_bypass-success = Успешно добавлен обход для пользователя '{ $user }'
