<div align="center">

# 🪐 Hullrot: Eclipsion

**A continuation of HULLROT with its own vision and direction, built on Space Station 14.**

[![Discord](https://img.shields.io/discord/1318776836599320657?style=for-the-badge&logo=discord&logoColor=white&label=Discord&color=%237289da)](https://discord.gg/tUbZ7CK7DC)
[![License](https://img.shields.io/badge/code-AGPLv3-blue?style=for-the-badge)](./LEGAL.md)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?&style=for-the-badge)](https://dotnet.microsoft.com/)

[Discord](https://discord.gg/tUbZ7CK7DC) • [Legal](./LEGAL.md)

</div>

---

## 📋 About the Project

**Hullrot: Eclipsion** is a fork of [Space Station 14](https://github.com/space-wizards/space-station-14), a space station
simulation game built on the [Robust Toolbox](https://github.com/space-wizards/RobustToolbox) engine.

The project is the continuation of [HULLROT](https://github.com/Sector-Crescent/Hullrot) and carries its direction
forward: a roleplay-heavy, faction-driven setting in the frontier region of Taypan, with ship-to-ship combat,
persistent factions and a strong focus on atmosphere over round-based deathmatch.

## 🚀 Quick Start

### Requirements

- **Git** — [download](https://git-scm.com/downloads)
- **.NET SDK 10.0.302 or higher** — [download](https://dotnet.microsoft.com/download/dotnet/10.0)

### 🍃 Windows

```sh
# 1. Clone the repository
git clone https://github.com/eclipsion-team/eclipsion.git
cd eclipsion

# 2. Download the engine
git submodule update --init --recursive

# 3. Build the project
Scripts\bat\buildAllRelease.bat

# 4. Run the client and server
Scripts\bat\runQuickAll.bat
```

**Done!** Connect to **localhost** in the client and start playing 🎮

### 🐧 Linux / macOS

```sh
# 1. Clone the repository
git clone https://github.com/eclipsion-team/eclipsion.git
cd eclipsion

# 2. Download the engine
git submodule update --init --recursive

# 3. Build the project
chmod +x Scripts/sh/buildAllRelease.sh
Scripts/sh/buildAllRelease.sh

# 4. Run the client and server
chmod +x Scripts/sh/runQuickAll.sh
Scripts/sh/runQuickAll.sh
```

**Done!** Connect to **localhost** in the client and start playing 🎮

## 🧬 Attribution

This project is a downstream fork and contains code and assets from many other Space Station 14 projects.
Content originating from another project is kept under a directory prefix (for example `_Goobstation/`, `_NF/`,
`_EE/`) or inside a dedicated `Content.<Project>.*` assembly, and carries the original authors' copyright
notices in per-file `SPDX-FileCopyrightText` headers and in each asset's `meta.json`.

Major upstreams include:

| Project | Used as |
| --- | --- |
| [Space Station 14](https://github.com/space-wizards/space-station-14) | Base game |
| [Robust Toolbox](https://github.com/space-wizards/RobustToolbox) | Engine (submodule) |
| [Einstein Engines](https://github.com/Simple-Station/Einstein-Engines) | `_EE`, `_SimpleStation`, `SimpleStation14` |
| [Goob-Station](https://github.com/Goob-Station/Goob-Station) | `_Goobstation`, `Content.Goobstation.*` |
| [Frontier Station 14](https://github.com/new-frontiers-14/frontier-station-14) | `_NF` |
| [Delta-v](https://github.com/DeltaV-Station/Delta-v) | `_DV`, `DeltaV` |
| [Nyanotrasen](https://github.com/Nyanotrasen/Nyanotrasen) | `Nyanotrasen` |
| [RMC-14](https://github.com/RMC-14/RMC-14) | `_RMC14` |
| [tgstation](https://github.com/tgstation/tgstation) | `_TG` (assets) |
| [Shiptest](https://github.com/shiptest-ss13/Shiptest) | Sprites and other assets; see each asset's metadata |
| [HULLROT](https://github.com/Sector-Crescent/Hullrot) | `_Crescent` — the fork this project continues |

Additional upstreams are present under the prefixes `_ADT`, `_Arcadis`, `_CS4875`, `_Corvax`, `_DEN`,
`_Funkystation`, `_Harmony`, `_Imp`, `_Impstation`, `_Lavaland`, `_Mono`, `_Nuclear14`, `_Shitmed`,
`_Starlight`, `_White`, `_ds14`, `Corvax`, `EstacaoPirata` and `WhiteDream`.
Refer to the per-file headers and `meta.json` metadata for the authoritative copyright and licensing of any
individual file.

Shiptest-derived assets retain the CC-BY-SA 3.0 license unless an asset carries documented evidence of a
different upstream license. Their source and modification notices are recorded in the containing `meta.json`.
The attribution notice shipped with the game resources is available at
[Resources/ShiptestAttribution.txt](./Resources/ShiptestAttribution.txt).

## 📜 License

The project code is licensed under **GNU AGPLv3** (code contributed before the cutoff commit noted in
[LEGAL.md](./LEGAL.md) is MIT).
Assets use various licenses, primarily CC-BY-SA 3.0. Some assets are **non-commercial** - see
[LEGAL.md](./LEGAL.md) before using this project commercially.

Because this project is AGPLv3, anyone who plays on a server running it is entitled to its source code.
The complete corresponding source is this repository: <https://github.com/eclipsion-team/eclipsion>.

For detailed license information, see [LEGAL.md](./LEGAL.md).
