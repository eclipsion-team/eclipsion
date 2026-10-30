using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Decals;
using Content.Server.Nutrition.EntitySystems;
using Content.Server.Popups;
using Content.Shared.Crayon;
using Content.Shared.Database;
using Content.Shared.Decals;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Crayon;

public sealed class CrayonSystem : SharedCrayonSystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IChatManager _chat = default!;

    private const string SootProtocolDrawing = "thank you";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CrayonComponent, ComponentInit>(OnCrayonInit);
        SubscribeLocalEvent<CrayonComponent, CrayonSelectMessage>(OnCrayonBoundUI);
        SubscribeLocalEvent<CrayonComponent, CrayonColorMessage>(OnCrayonBoundUIColor);
        SubscribeLocalEvent<CrayonComponent, UseInHandEvent>(OnCrayonUse, before: new[] { typeof(FoodSystem) });
        SubscribeLocalEvent<CrayonComponent, AfterInteractEvent>(OnCrayonAfterInteract, after: new[] { typeof(FoodSystem) });
        SubscribeLocalEvent<CrayonComponent, DroppedEvent>(OnCrayonDropped);
        SubscribeLocalEvent<CrayonComponent, ComponentGetState>(OnCrayonGetState);
    }

    private static void OnCrayonGetState(EntityUid uid, CrayonComponent component, ref ComponentGetState args)
    {
        args.State = new CrayonComponentState(component.Color, component.SelectedState, component.Charges, component.Capacity);
    }

    private void OnCrayonAfterInteract(EntityUid uid, CrayonComponent component, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        if (component.Charges <= 0)
        {
            if (component.DeleteEmpty)
                UseUpCrayon(uid, args.User);
            else
                _popup.PopupEntity(Loc.GetString("crayon-interact-not-enough-left-text"), uid, args.User);

            args.Handled = true;
            return;
        }

        if (!args.ClickLocation.IsValid(EntityManager))
        {
            _popup.PopupEntity(Loc.GetString("crayon-interact-invalid-location"), uid, args.User);
            args.Handled = true;
            return;
        }

        // eclipsion - soot contingency protocol, whatever they meant to draw comes out as a thank you
        var drawn = component.SelectedState;
        if (TryComp<ActorComponent>(args.User, out var actor) && _chat.IsUnderSootProtocol(actor.PlayerSession))
        {
            if (!DrawThankYou(args.ClickLocation, component.Color))
                return;

            drawn = SootProtocolDrawing;
        }
        else if (!_decals.TryAddDecal(component.SelectedState, args.ClickLocation.Offset(new Vector2(-0.5f, -0.5f)), out _, component.Color, cleanable: true))
            return;

        if (component.UseSound != null)
            _audio.PlayPvs(component.UseSound, uid, AudioParams.Default.WithVariation(0.125f));

        // Frontier: check if crayon is infinite, Delta V Port
        if (component.Charges != int.MaxValue)
        {
            // Decrease "Ammo"
            component.Charges--;
            Dirty(uid, component);
        }
        // End Frontier, Delta V Port

        _adminLogger.Add(LogType.CrayonDraw, LogImpact.Low, $"{EntityManager.ToPrettyString(args.User):user} drew a {component.Color:color} {drawn}");
        args.Handled = true;

        if (component.DeleteEmpty && component.Charges <= 0)
            UseUpCrayon(uid, args.User);
        else
            _uiSystem.ServerSendUiMessage(uid, SharedCrayonComponent.CrayonUiKey.Key, new CrayonUsedMessage(component.SelectedState));
    }

    // letter glyphs are small, so they sit closer than a tile apart
    private bool DrawThankYou(EntityCoordinates click, Color color)
    {
        const float spacing = 0.4f;
        var start = -(SootProtocolDrawing.Length - 1) * spacing / 2f;
        var any = false;

        for (var i = 0; i < SootProtocolDrawing.Length; i++)
        {
            var letter = SootProtocolDrawing[i];
            if (letter == ' ')
                continue;

            var offset = new Vector2(start + i * spacing - 0.5f, -0.5f);
            any |= _decals.TryAddDecal(letter.ToString(), click.Offset(offset), out _, color, cleanable: true);
        }

        return any;
    }

    private void OnCrayonUse(EntityUid uid, CrayonComponent component, UseInHandEvent args)
    {
        // Open crayon window if neccessary.
        if (args.Handled)
            return;

        if (!_uiSystem.HasUi(uid, SharedCrayonComponent.CrayonUiKey.Key))
        {
            return;
        }

        _uiSystem.TryToggleUi(uid, SharedCrayonComponent.CrayonUiKey.Key, args.User);

        _uiSystem.SetUiState(uid, SharedCrayonComponent.CrayonUiKey.Key, new CrayonBoundUserInterfaceState(component.SelectedState, component.SelectableColor, component.Color));
        args.Handled = true;
    }

    private void OnCrayonBoundUI(EntityUid uid, CrayonComponent component, CrayonSelectMessage args)
    {
        // Check if the selected state is valid
        if (!_prototypeManager.TryIndex<DecalPrototype>(args.State, out var prototype) || !prototype.Tags.Contains("crayon"))
            return;

        component.SelectedState = args.State;

        Dirty(uid, component);
    }

    private void OnCrayonBoundUIColor(EntityUid uid, CrayonComponent component, CrayonColorMessage args)
    {
        // you still need to ensure that the given color is a valid color
        if (!component.SelectableColor || args.Color == component.Color)
            return;

        component.Color = args.Color;
        Dirty(uid, component);

    }

    private void OnCrayonInit(EntityUid uid, CrayonComponent component, ComponentInit args)
    {
        component.Charges = component.Capacity;

        // Get the first one from the catalog and set it as default
        var decal = _prototypeManager.EnumeratePrototypes<DecalPrototype>().FirstOrDefault(x => x.Tags.Contains("crayon"));
        component.SelectedState = decal?.ID ?? string.Empty;
        Dirty(uid, component);
    }

    private void OnCrayonDropped(EntityUid uid, CrayonComponent component, DroppedEvent args)
    {
        // TODO: Use the existing event.
        _uiSystem.CloseUi(uid, SharedCrayonComponent.CrayonUiKey.Key, args.User);
    }

    private void UseUpCrayon(EntityUid uid, EntityUid user)
    {
        _popup.PopupEntity(Loc.GetString("crayon-interact-used-up-text", ("owner", uid)), user, user);
        EntityManager.QueueDeleteEntity(uid);
    }
}
