lag-monitor-gc = Garbage collector
lag-monitor-unknown-cause = nothing measured stood out (outside entity systems)

lag-monitor-alert-hitch = LAG: the server froze for {$ms} ms. Took the time: {$cause}. Type lagmonitor for details.
lag-monitor-alert-cpu = LAG: the server can't keep up (load {$load}%, {$tps}/{$target} ticks per second). Heaviest: {$cause}. Type lagmonitor for details.

lag-monitor-finding-cpu = The server can't keep up: load {$load}%, running {$tps} of {$target} ticks per second. Everyone gets input lag. Heaviest: {$cause}.
lag-monitor-finding-busy = The server is busy ({$load}% load) but still keeping up. Heaviest: {$cause}.
lag-monitor-finding-hitch = {$count} freeze(s) in the last 5 minutes, worst {$ms} ms. Worst one: {$cause}.
lag-monitor-finding-gc = The garbage collector paused the server for {$ms} ms in the last 5 seconds (too much memory being thrown away).
lag-monitor-finding-net = Sending {$kb} KB/s to each player. Big UI or entity states? Check consoles people have open.
lag-monitor-finding-ping = {$count} player(s) with ping over {$ping} ms. Their lag is their connection, not the server.
lag-monitor-finding-ok = The server is keeping up. If someone still lags, it is their connection or their FPS.

lag-monitor-window-title = Lag Monitor
lag-monitor-disabled = lagmonitor.enabled is off, nothing is being measured.
lag-monitor-waiting = Waiting for the first sample (every 5 seconds)...
lag-monitor-view-now = Now
lag-monitor-view-peak = Worst of the last 10 min
lag-monitor-peak-at = Sample from {$time}
lag-monitor-clear = Clear freezes
lag-monitor-load = Load
lag-monitor-tickrate = Ticks/s
lag-monitor-tickms = ms per tick
lag-monitor-players = Players
lag-monitor-entities = Entities
lag-monitor-npcs = NPCs (active/all)
lag-monitor-grids = Grids
lag-monitor-drones = POI drones
lag-monitor-bodies = Moving bodies
lag-monitor-net = Net out / in
lag-monitor-gc-pause = GC pause
lag-monitor-findings = What it looks like
lag-monitor-history = Load, last 10 minutes (line = 100%)
lag-monitor-systems = Heaviest systems (ms per tick)
lag-monitor-parts = Tick parts (ms per tick)
lag-monitor-hitches = Freezes (longest gaps between ticks)
lag-monitor-no-hitches = No freezes recorded.
lag-monitor-round-start = (round start)
lag-monitor-pings = Highest ping
lag-monitor-budget = Budget per tick: {$ms} ms
lag-monitor-self = This monitor (per tick)
