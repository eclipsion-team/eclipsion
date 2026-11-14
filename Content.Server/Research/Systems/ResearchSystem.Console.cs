using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Components;
using Content.Shared.UserInterface;
using Content.Shared.Access.Components;
using Content.Shared.Emag.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared._Goobstation.Research;

namespace Content.Server.Research.Systems;

public sealed partial class ResearchSystem
{
    private void InitializeConsole()
    {
        SubscribeLocalEvent<ResearchConsoleComponent, ConsoleUnlockTechnologyMessage>(OnConsoleUnlock);
        SubscribeLocalEvent<ResearchConsoleComponent, BeforeActivatableUIOpenEvent>(OnConsoleBeforeUiOpened);
        SubscribeLocalEvent<ResearchConsoleComponent, ResearchServerPointsChangedEvent>(OnPointsChanged);
        SubscribeLocalEvent<ResearchConsoleComponent, ResearchRegistrationChangedEvent>(OnConsoleRegistrationChanged);
        SubscribeLocalEvent<ResearchConsoleComponent, TechnologyDatabaseModifiedEvent>(OnConsoleDatabaseModified);
    }

    private void OnConsoleUnlock(EntityUid uid, ResearchConsoleComponent component, ConsoleUnlockTechnologyMessage args)
    {
        var act = args.Actor;

        if (!this.IsPowered(uid, EntityManager))
            return;

        if (!PrototypeManager.TryIndex<TechnologyPrototype>(args.Id, out var technologyPrototype))
            return;

        // don't trust the client button, check the server's list
        if (!TryComp<ResearchClientComponent>(uid, out var researchClient) ||
            researchClient.Server is not { } serverUid ||
            !TryComp<ResearchServerComponent>(serverUid, out var researchServer) ||
            !TryComp<TechnologyDatabaseComponent>(serverUid, out var serverDatabase) ||
            !serverDatabase.CurrentTechnologyCards.Contains(args.Id))
        {
            return;
        }

        if (TryComp<AccessReaderComponent>(uid, out var access) && !_accessReader.IsAllowed(act, uid, access))
        {
            _popup.PopupEntity(Loc.GetString("research-console-no-access-popup"), act);
            return;
        }

        var cost = GetTechnologyCost(technologyPrototype, researchServer);
        if (!UnlockTechnology(uid, args.Id, act))
            return;

        if (!HasComp<EmaggedComponent>(uid))
        {
            var getIdentityEvent = new TryGetIdentityShortInfoEvent(uid, act);
            RaiseLocalEvent(getIdentityEvent);

            var message = Loc.GetString(
                "research-console-unlock-technology-radio-broadcast",
                ("technology", Loc.GetString(technologyPrototype.Name)),
                ("amount", cost),
                ("approver", getIdentityEvent.Title ?? string.Empty)
            );
            _radio.SendRadioMessage(uid, message, component.AnnouncementChannel, uid, escapeMarkup: false);
        }

        SyncClientWithServer(uid);
        UpdateConsoleInterface(uid, component);
    }

    private void OnConsoleBeforeUiOpened(EntityUid uid, ResearchConsoleComponent component, BeforeActivatableUIOpenEvent args)
    {
        // old saves still have the 3 random cards in them
        if (TryComp<ResearchClientComponent>(uid, out var client) && client.Server is { } server)
            UpdateTechnologyCards(server);

        SyncClientWithServer(uid);
        component.LastUiState = null;
        UpdateConsoleInterface(uid, component);
    }

    private void UpdateConsoleInterface(EntityUid uid, ResearchConsoleComponent? component = null, ResearchClientComponent? clientComponent = null)
    {
        if (!Resolve(uid, ref component, ref clientComponent, false))
            return;

        ResearchConsoleBoundInterfaceState state;

        if (TryGetClientServer(uid, out _, out var serverComponent, clientComponent))
        {
            var points = clientComponent.ConnectedToServer ? serverComponent.Points : 0;
            var softCap = clientComponent.ConnectedToServer ? serverComponent.CurrentSoftCapMultiplier : 1;

            var researches = new Dictionary<string, ResearchAvailability>();
            if (clientComponent.ConnectedToServer &&
                TryComp<TechnologyDatabaseComponent>(clientComponent.Server, out var database))
            {
                var unlocked = new HashSet<string>(database.UnlockedTechnologies.Select(static t => t.Id));
                var disciplineTiers = GetDisciplineTiers(database);

                researches = PrototypeManager.EnumeratePrototypes<TechnologyPrototype>()
                    .Where(technology => !technology.Hidden &&
                        database.SupportedDisciplines.Contains(technology.Discipline))
                    .ToDictionary(
                        technology => technology.ID,
                        technology =>
                        {
                            if (unlocked.Contains(technology.ID))
                                return ResearchAvailability.Researched;

                            if (!IsTechnologyAvailable(database, technology, disciplineTiers))
                                return ResearchAvailability.Unavailable;

                            var effectiveCost = technology.Cost * softCap;
                            return database.CurrentTechnologyCards.Contains(technology.ID) &&
                                   points >= effectiveCost
                                ? ResearchAvailability.Available
                                : ResearchAvailability.PrereqsMet;
                        });
            }

            state = new ResearchConsoleBoundInterfaceState(points, softCap, researches);
        }
        else
        {
            state = new ResearchConsoleBoundInterfaceState(default, 1);
        }

        if (component.LastUiState is { } previous &&
            previous.SoftCapMultiplier.Equals(state.SoftCapMultiplier) &&
            ResearchAvailabilityHelper.ResearchesEqual(previous.Researches, state.Researches))
        {
            if (previous.Points != state.Points)
            {
                _uiSystem.ServerSendUiMessage(
                    uid,
                    ResearchConsoleUiKey.Key,
                    new ResearchConsolePointsChangedMessage(state.Points));
            }

            component.LastUiState = state;
            return;
        }

        component.LastUiState = state;
        _uiSystem.SetUiState(uid, ResearchConsoleUiKey.Key, state);
    }

    private void OnPointsChanged(EntityUid uid, ResearchConsoleComponent component, ref ResearchServerPointsChangedEvent args)
    {
        if (!_uiSystem.IsUiOpen(uid, ResearchConsoleUiKey.Key))
            return;
        UpdateConsoleInterface(uid, component);
    }

    private void OnConsoleRegistrationChanged(EntityUid uid, ResearchConsoleComponent component, ref ResearchRegistrationChangedEvent args)
    {
        SyncClientWithServer(uid);
        if (!_uiSystem.IsUiOpen(uid, ResearchConsoleUiKey.Key))
            return;
        UpdateConsoleInterface(uid, component);
    }

    private void OnConsoleDatabaseModified(EntityUid uid, ResearchConsoleComponent component, ref TechnologyDatabaseModifiedEvent args)
    {
        if (!_uiSystem.IsUiOpen(uid, ResearchConsoleUiKey.Key))
            return;
        UpdateConsoleInterface(uid, component);
    }

}
