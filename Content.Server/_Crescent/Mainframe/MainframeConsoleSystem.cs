using Content.Shared._Crescent.AlertConsole;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared._Crescent.Mainframe;
using Content.Shared._Crescent.Overwatch;
using Content.Shared._Crescent.Payment;
using Content.Shared._Crescent.PoliticalMap;
using Content.Shared._Crescent.Territory;
using Content.Shared.Cargo;
using Content.Shared.Communications;
using Robust.Server.GameObjects;

namespace Content.Server._Crescent.Mainframe;

/// <summary>
/// Fans a mainframe's single ActivatableUI key out to every console UI it hosts.
/// </summary>
/// <remarks>
/// Each hosted console keeps its own key, state and messages, so their systems need no changes —
/// this only makes sure all their UIs open and close together with the shell.
/// </remarks>
public sealed class MainframeConsoleSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    /// <summary>
    /// Keys the mainframe opens alongside its own. A key not declared in the entity's UserInterface
    /// block is skipped, so a mainframe may host any subset of these.
    /// </summary>
    private static readonly Enum[] SubKeys =
    {
        OverwatchUiKey.Key,
        DiplomacyConsoleUiKey.Key,
        AlertConsoleUiKey.Key,
        EconomicConsoleUiKey.Key,
        PaymentConsoleUiKey.Key,
        CommunicationsConsoleUiKey.Key,
        TerritoryConsoleUiKey.Key,
        PoliticalMapUiKey.Key,
    };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MainframeConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<MainframeConsoleComponent, BoundUIClosedEvent>(OnClosed);
    }

    private void OnOpened(EntityUid uid, MainframeConsoleComponent component, BoundUIOpenedEvent args)
    {
        // Unfiltered subscription: without this the sub-key opens below would re-enter and recurse.
        if (!Equals(args.UiKey, MainframeUiKey.Key))
            return;

        foreach (var key in SubKeys)
        {
            if (_ui.HasUi(uid, key))
                _ui.OpenUi(uid, key, args.Actor);
        }
    }

    private void OnClosed(EntityUid uid, MainframeConsoleComponent component, BoundUIClosedEvent args)
    {
        if (!Equals(args.UiKey, MainframeUiKey.Key))
            return;

        // Closing only the keys we opened, rather than CloseUis(), which would also take out
        // unrelated interfaces on the entity such as the wires panel.
        foreach (var key in SubKeys)
        {
            if (_ui.HasUi(uid, key))
                _ui.CloseUi(uid, key, args.Actor);
        }
    }
}
