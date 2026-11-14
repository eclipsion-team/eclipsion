using Content.Shared.Crescent.Radar;
using Content.Server.Shuttles.Systems;
using Robust.Shared.Timing;

namespace Content.Server.Crescent.Radar;

public sealed partial class TurretIFFSystem : SharedTurretIFFSystem
{
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly RadarConsoleSystem _radarConsole = default!;

    [Dependency] private readonly IGameTiming _timing = default!;

    // Refreshing is expensive, so batch turret changes and refresh at most every RefreshInterval.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(0.5);
    private bool _turretsDirty;
    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TurretIFFComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<TurretIFFComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(EntityUid uid, TurretIFFComponent component, ComponentStartup args)
    {
        if (EntityManager.GetComponent<MetaDataComponent>(uid).EntityLifeStage < EntityLifeStage.Initialized)
        {
            return;
        }

        _turretsDirty = true;
    }

    private void OnShutdown(EntityUid uid, TurretIFFComponent component, ComponentShutdown args)
    {
        // destroyed turrets have to refresh the radar too
        _turretsDirty = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_turretsDirty || _timing.CurTime < _nextRefresh)
            return;

        _turretsDirty = false;
        _nextRefresh = _timing.CurTime + RefreshInterval;

        _shuttleConsole.RefreshIFFState();
        _radarConsole.RefreshIFFState();
    }
}
