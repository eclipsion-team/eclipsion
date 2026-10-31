using Content.Shared._Crescent.Territory;

namespace Content.Client._Crescent.Territory;

/// <summary>
/// Holds the territory spawn beacons the server says can take a late-joiner, for the late-join window.
/// </summary>
public sealed class TerritoryDeploySystem : EntitySystem
{
    public IReadOnlyList<TerritoryDeployPoint> Points { get; private set; } = new List<TerritoryDeployPoint>();

    public event Action? Updated;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<TerritoryDeployPointsEvent>(OnPoints);
    }

    private void OnPoints(TerritoryDeployPointsEvent ev)
    {
        Points = ev.Points;
        Updated?.Invoke();
    }
}
