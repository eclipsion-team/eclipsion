using Content.Shared._Crescent.PoliticalMap;

namespace Content.Client._Crescent.PoliticalMap;

public sealed class PoliticalMapSystem : EntitySystem
{
    public PoliticalMapData? Data { get; private set; }

    public event Action<PoliticalMapData>? Updated;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<PoliticalMapUpdateEvent>(OnUpdate);
    }

    public void Request()
    {
        RaiseNetworkEvent(new PoliticalMapRequestEvent());
    }

    private void OnUpdate(PoliticalMapUpdateEvent ev)
    {
        Data = ev.Data;
        Updated?.Invoke(ev.Data);
    }
}
