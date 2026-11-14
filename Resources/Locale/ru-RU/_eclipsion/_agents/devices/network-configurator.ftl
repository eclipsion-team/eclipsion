network-configurator-ui-count-label = {$count ->
    [one] {$count} устройство
    [few] {$count} устройства
   *[other] {$count} устройств
}
network-configurator-link-buffer-added = {$device} добавлено в буфер связей ({$count})
network-configurator-link-buffer-removed = {$device} убрано из буфера связей ({$count})
network-configurator-link-buffer-full = Буфер связей заполнен!
network-configurator-link-buffer-cleared = Буфер связей очищен.
network-configurator-links-added = Создано {$count} {$count ->
    [one] соединение
    [few] соединения
   *[other] соединений
}.
network-configurator-links-removed = Удалено {$count} {$count ->
    [one] соединение
    [few] соединения
   *[other] соединений
}.
network-configurator-links-none = Нечего связывать.
network-configurator-link-buffer-add = Добавить в буфер связей
network-configurator-link-buffer-remove = Убрать из буфера связей
network-configurator-link-buffer-clear = Забыть все устройства
network-configurator-link-defaults-buffer = Связать по умолчанию с буфером
network-configurator-open-link-menu = Открыть меню связей
network-configurator-examine-link-buffer = Устройств в буфере связей: [color=yellow]{$count}[/color]
network-configurator-item-status-buffer = Буфер связей: [color=yellow]{$count}[/color]
network-configurator-link-title = Связывание устройств
network-configurator-link-help = Кликайте по устройствам мультитулом в режиме связывания, чтобы добавить их сюда. Выберите источники и приёмники, затем нажмите порт источника и порт приёмника, чтобы связать сразу все выбранные пары.
network-configurator-link-sources = Источники ({$count})
network-configurator-link-sinks = Приёмники ({$count})
network-configurator-link-ports = Порты
network-configurator-link-select-hint = Выберите хотя бы один источник и один приёмник.
network-configurator-link-no-sources = В буфере нет источников.
network-configurator-link-no-sinks = В буфере нет приёмников.
network-configurator-link-no-links = Связей пока нет.
network-configurator-link-existing = Существующие связи ({$count})
network-configurator-link-only-selected = Только выбранные
network-configurator-link-forget-device = Убрать из буфера связей
network-configurator-link-device-tooltip = Адрес: {$address}
    Связи: {$links}
network-configurator-link-row = [color=#8fd3ff]{$source}[/color] [color=#a0a0a0]({$sourcePort})[/color]  ➝  [color=#8fd3ff]{$sink}[/color] [color=#a0a0a0]({$sinkPort})[/color]
network-configurator-link-unlink = Отвязать
network-configurator-link-clear-selected = Очистить связи
network-configurator-link-clear-selected-tooltip = Удаляет все связи между выбранными источниками и приёмниками
network-configurator-link-defaults-tooltip = Добавляет связи по умолчанию между каждым выбранным источником и приёмником
