using System.Linq;
using Content.Shared._Crescent.HeatSeeking;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Crescent.HeatSeeking;

/// <summary>
/// Flags a grid's shuttle consoles when something has locked onto its hull, so a missile isn't the first
/// the pilot hears of it. Only whoever is flying the ship is told; the rest of the crew isn't spammed.
/// </summary>
public sealed class HeatSeekingWarningSystem : EntitySystem
{
    private const float ScanInterval = 0.25f;

    // Per grid, otherwise a salvo of eight sets off eight buzzers.
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromSeconds(6);

    private static readonly SoundPathSpecifier WarningSound =
        new("/Audio/Machines/warning_buzzer.ogg", AudioParams.Default.WithVolume(-4f));

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _nextWarning = new();

    private readonly Dictionary<EntityUid, int> _lockedGrids = new();

    private readonly HashSet<EntityUid> _warningGrids = new();

    private float _scanAccumulator;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _scanAccumulator += frameTime;
        if (_scanAccumulator < ScanInterval)
            return;

        _scanAccumulator = 0f;

        _lockedGrids.Clear();

        var query = EntityQueryEnumerator<HeatSeekingComponent>();
        while (query.MoveNext(out _, out var seeker))
        {
            if (seeker.TargetEntity is not { } target || !Exists(target))
                continue;

            if (Transform(target).GridUid is { } grid)
                _lockedGrids[grid] = _lockedGrids.GetValueOrDefault(grid) + 1;
        }

        UpdateLockComponents();

        if (_lockedGrids.Count == 0)
        {
            _nextWarning.Clear();
            return;
        }

        var now = _timing.CurTime;
        _warningGrids.Clear();

        foreach (var grid in _lockedGrids.Keys)
        {
            if (_nextWarning.TryGetValue(grid, out var next) && now < next)
                continue;

            _nextWarning[grid] = now + WarningCooldown;
            _warningGrids.Add(grid);
        }

        if (_warningGrids.Count > 0)
            WarnPilots();

        if (_nextWarning.Count > _lockedGrids.Count)
        {
            foreach (var grid in _nextWarning.Keys.ToArray())
            {
                if (!_lockedGrids.ContainsKey(grid))
                    _nextWarning.Remove(grid);
            }
        }
    }

    private void UpdateLockComponents()
    {
        var locks = EntityQueryEnumerator<MissileLockWarningComponent>();
        while (locks.MoveNext(out var grid, out _))
        {
            if (!_lockedGrids.ContainsKey(grid))
                RemCompDeferred<MissileLockWarningComponent>(grid);
        }

        foreach (var (grid, seekers) in _lockedGrids)
        {
            if (TerminatingOrDeleted(grid))
                continue;

            var warning = EnsureComp<MissileLockWarningComponent>(grid);
            if (warning.Seekers == seekers)
                continue;

            warning.Seekers = seekers;
            Dirty(grid, warning);
        }
    }

    private void WarnPilots()
    {
        var pilots = EntityQueryEnumerator<PilotComponent, ActorComponent>();
        while (pilots.MoveNext(out _, out var pilot, out var actor))
        {
            if (pilot.Console is not { } console ||
                Transform(console).GridUid is not { } grid ||
                !_warningGrids.Contains(grid))
            {
                continue;
            }

            _audio.PlayGlobal(WarningSound, actor.PlayerSession);
        }
    }
}
