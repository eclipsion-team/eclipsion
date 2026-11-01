using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._Crescent.Shuttles.Events;

namespace Content.Server._Crescent.Shuttles.Systems;

public sealed class ShuttleTurnRateSystem : EntitySystem
{
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleConsoleComponent, ShuttleTurnRateMessage>(OnTurnRate);
    }

    private void OnTurnRate(EntityUid uid, ShuttleConsoleComponent component, ShuttleTurnRateMessage args)
    {
        if (!float.IsFinite(args.TurnRate))
            return;

        // Use the console's own grid (or its drone target), never a client-supplied one.
        var getShuttleEv = new ConsoleShuttleEvent { Console = uid };
        RaiseLocalEvent(uid, ref getShuttleEv);

        if (getShuttleEv.Console is not { } console ||
            Transform(console).GridUid is not { } grid ||
            !TryComp<ShuttleComponent>(grid, out var shuttle))
            return;

        shuttle.TurnRate = Math.Clamp(args.TurnRate, ShuttleComponent.MinTurnRate, 1f);
        _console.RefreshShuttleConsoles(grid);
    }
}
