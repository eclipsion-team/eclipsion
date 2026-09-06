# Blood cult — disabled

Every `.yml` in this folder is commented out and **nothing here loads**. The prototypes are kept only
as a reference for a future revival; the sprites and sounds they point at are still in the repo at
`Resources/Textures/WhiteDream/` and `Resources/Audio/WhiteDream/`.

The C# that backed all of this (`Content.Server/WhiteDream`, `Content.Shared/WhiteDream`,
`Content.Client/WhiteDream`) was removed. Recover it from git if you bring the mode back:

    git checkout 8135a85e71f -- Content.Server/WhiteDream Content.Shared/WhiteDream Content.Client/WhiteDream

The English locale went with it and lives in the same commit at `Resources/Locale/en-US/white-dream`.

## Read this before reviving anything

Six handlers took an entity id or a prototype id straight off the client and acted on it without
checking it against the list the server had actually offered. Fix them first — as written they are
arbitrary-spawn and arbitrary-teleport primitives for anyone who can open the UI:

- `TimedFactorySystem.OnPrototypeSelected` — `Spawn(args.SelectedItem, ...)`, no check against `Entries`
- `ConstructShellSystem.OnConstructSelected` — same, plus a mind transfer into whatever spawned
- `BloodRitesSystem.OnRitesMessage` — `Spawn(args.SelectedProto, ...)`, no check against `Crafts`
- `CultRuneTeleportSystem.OnTeleportRuneSelected` — teleports to any entity in the world
- `CultRuneSummonSystem.OnCultistSelected` — summons any entity, not just cultists
- `VoidTorchSystem.OnCultistSelected` — puts an item in any entity's hands

`FactionRecruitmentConsoleSystem.OnAssign` shows the pattern these are missing: re-check the id
against the console's own list, then `TryIndex` it.

Two things outside this folder were kept because live content depends on them, so do not re-add them
here: `SummonEquipmentEvent` (now `Content.Shared/Psionics/Events/`, used by the mantis' Summon Black
Blade) and `RitualDagger` (now in `Entities/Objects/Weapons/Melee/cult.yml`, sold by the Crescent
black market and made by an NF lathe).
