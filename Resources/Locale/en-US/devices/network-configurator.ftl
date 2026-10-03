# Popups

network-configurator-device-saved = Successfully saved network device {$device} with address {$address}!
network-configurator-device-failed = Failed to save network device {$device}! No address assigned!
network-configurator-too-many-devices = Too many devices stored on this device!
network-configurator-update-ok = Device storage updated.
network-configurator-device-already-saved = network device: {$device} is already saved.
network-configurator-device-access-denied = Access denied!
network-configurator-link-mode-started = Started linking device: {$device}
network-configurator-link-mode-stopped = Stopped linking.
network-configurator-mode-link = Link
network-configurator-mode-list = List
network-configurator-switched-mode = Switched mode to: {$mode}

# Verbs
network-configurator-save-device = Save device
network-configurator-configure = Configure
network-configurator-switch-mode = Switch mode
network-configurator-link-defaults = Link defaults
network-configurator-start-link = Start link
network-configurator-link = Link

# ui
network-configurator-title-saved-devices = Saved Devices
network-configurator-title-device-configuration = Device Configuration
network-configurator-ui-clear-button = Clear
network-configurator-ui-count-label = {$count} Devices

# tooltips
network-configurator-tooltip-set = Sets targets device list
network-configurator-tooltip-add = Adds to targets device list
network-configurator-tooltip-edit = Edit targets device list
network-configurator-tooltip-clear = Clear targets device list
network-configurator-tooltip-copy = Copy targets device list to held tool
network-configurator-tooltip-show = Show a holographic visualization of targets device list

# examine
network-configurator-examine-mode-link = [color=red]Link[/color]
network-configurator-examine-mode-list = [color=green]List[/color]
network-configurator-examine-current-mode = Current mode: {$mode}
network-configurator-examine-switch-modes = Press {$key} to switch modes

# item status
network-configurator-item-status-label = Mode: {$mode}
    Switch: {$keybinding}

# Link buffer
network-configurator-link-buffer-added = Added {$device} to the link buffer ({$count})
network-configurator-link-buffer-removed = Removed {$device} from the link buffer ({$count})
network-configurator-link-buffer-full = The link buffer is full!
network-configurator-link-buffer-cleared = Link buffer cleared.
network-configurator-links-added = Linked {$count} {$count ->
    [one] connection
   *[other] connections
}.
network-configurator-links-removed = Removed {$count} {$count ->
    [one] connection
   *[other] connections
}.
network-configurator-links-none = Nothing to link.

network-configurator-link-buffer-add = Add to link buffer
network-configurator-link-buffer-remove = Remove from link buffer
network-configurator-link-buffer-clear = Forget all devices
network-configurator-link-defaults-buffer = Link defaults with buffer
network-configurator-open-link-menu = Open link menu

network-configurator-examine-link-buffer = Devices in link buffer: [color=yellow]{$count}[/color]
network-configurator-item-status-buffer = Link buffer: [color=yellow]{$count}[/color]

# Link menu
network-configurator-link-title = Device Linking
network-configurator-link-help = Click devices with the multitool in link mode to add them here. Select sources and receivers, then click a source port and a receiver port to link every selected pair at once.
network-configurator-link-sources = Sources ({$count})
network-configurator-link-sinks = Receivers ({$count})
network-configurator-link-ports = Ports
network-configurator-link-select-all = All
network-configurator-link-select-hint = Select at least one source and one receiver.
network-configurator-link-no-sources = No sources buffered.
network-configurator-link-no-sinks = No receivers buffered.
network-configurator-link-no-links = No links yet.
network-configurator-link-existing = Existing links ({$count})
network-configurator-link-only-selected = Only selected
network-configurator-link-forget-device = Remove from the link buffer
network-configurator-link-device-tooltip = Address: {$address}
    Links: {$links}
network-configurator-link-row = [color=#8fd3ff]{$source}[/color] [color=#a0a0a0]({$sourcePort})[/color]  ➝  [color=#8fd3ff]{$sink}[/color] [color=#a0a0a0]({$sinkPort})[/color]
network-configurator-link-unlink = Unlink
network-configurator-link-clear-selected = Clear links
network-configurator-link-clear-selected-tooltip = Removes every link between the selected sources and receivers
network-configurator-link-defaults-tooltip = Adds the default links between every selected source and receiver
network-configurator-link-selection = {$sources} {$sources ->
    [one] source
   *[other] sources
} × {$sinks} {$sinks ->
    [one] receiver
   *[other] receivers
} = {$pairs} {$pairs ->
    [one] pair
   *[other] pairs
}
