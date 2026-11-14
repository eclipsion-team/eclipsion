using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Content.Client.Actions;
using Content.Client.Construction;
using Content.Client.Gameplay;
using Content.Client.Hands;
using Content.Client.Interaction;
using Content.Client.Outline;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.Actions.Controls;
using Content.Client.UserInterface.Systems.Actions.Widgets;
using Content.Client.UserInterface.Systems.Actions.Windows;
using Content.Client.UserInterface.Systems.Gameplay;
using Content.Shared.Actions;
using Content.Shared.Ghost;
using Content.Shared.Input;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using static Content.Client.Actions.ActionsSystem;
using static Content.Client.UserInterface.Systems.Actions.Windows.ActionsWindow;
using static Robust.Client.UserInterface.Control;
using static Robust.Client.UserInterface.Controls.BaseButton;
using static Robust.Client.UserInterface.Controls.LineEdit;
using static Robust.Client.UserInterface.Controls.MultiselectOptionButton<
    Content.Client.UserInterface.Systems.Actions.Windows.ActionsWindow.Filters>;
using static Robust.Client.UserInterface.Controls.TextureRect;
using static Robust.Shared.Input.Binding.PointerInputCmdHandler;

namespace Content.Client.UserInterface.Systems.Actions;

public sealed class ActionUIController : UIController, IOnStateChanged<GameplayState>, IOnSystemChanged<ActionsSystem>
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IInputManager _input = default!;

    [UISystemDependency] private readonly ActionsSystem? _actionsSystem = default;
    [UISystemDependency] private readonly InteractionOutlineSystem? _interactionOutline = default;
    [UISystemDependency] private readonly TargetOutlineSystem? _targetOutline = default;
    [UISystemDependency] private readonly SpriteSystem _spriteSystem = default!;

    private ActionButtonContainer? _container;

    /// <summary>
    /// Every auto-populated action, in one continuous list. The bar used to be split into fixed size
    /// pages you had to arrow through, which simply hid everything past the tenth action.
    /// </summary>
    private readonly ActionPage _page = new(ContentKeyFunctions.GetHotbarBoundKeys().Length);

    private readonly DragDropHelper<ActionButton> _menuDragHelper;
    private readonly TextureRect _dragShadow;
    private ActionsWindow? _window;

    // pinned slots per body, client only
    private const int MaxPins = 10;
    private readonly Dictionary<EntityUid, Dictionary<int, PinKey>> _pinsByBody = new();
    private readonly HashSet<EntityUid> _pinClaimed = new();

    private ActionsBar? ActionsBar => UIManager.GetActiveUIWidgetOrNull<ActionsBar>();
    private MenuButton? ActionButton => UIManager.GetActiveUIWidgetOrNull<MenuBar.Widgets.GameTopMenuBar>()?.ActionButton;

    public bool IsDragging => _menuDragHelper.IsDragging;

    /// <summary>
    /// Action slot we are currently selecting a target for.
    /// </summary>
    public EntityUid? SelectingTargetFor { get; private set; }

    public ActionUIController()
    {
        _menuDragHelper = new DragDropHelper<ActionButton>(OnMenuBeginDrag, OnMenuContinueDrag, OnMenuEndDrag);
        _dragShadow = new TextureRect
        {
            MinSize = new Vector2(64, 64),
            Stretch = StretchMode.Scale,
            Visible = false,
            SetSize = new Vector2(64, 64),
            MouseFilter = MouseFilterMode.Ignore,
        };
    }

    public override void Initialize()
    {
        base.Initialize();

        var gameplayStateLoad = UIManager.GetUIController<GameplayStateLoadController>();
        gameplayStateLoad.OnScreenLoad += OnScreenLoad;
        gameplayStateLoad.OnScreenUnload += OnScreenUnload;
    }

    private void OnScreenLoad()
    {
       LoadGui();
    }

    private void OnScreenUnload()
    {
        UnloadGui();
    }

    public void OnStateEntered(GameplayState state)
    {
        if (_actionsSystem != null)
        {
            _actionsSystem.OnActionAdded += OnActionAdded;
            _actionsSystem.OnActionRemoved += OnActionRemoved;
            _actionsSystem.ActionsUpdated += OnActionsUpdated;
        }

        UpdateFilterLabel();
        QueueWindowUpdate();

        _dragShadow.Orphan();
        UIManager.PopupRoot.AddChild(_dragShadow);

        var builder = CommandBinds.Builder;
        var hotbarKeys = ContentKeyFunctions.GetHotbarBoundKeys();
        for (var i = 0; i < hotbarKeys.Length; i++)
        {
            var boundId = i; // This is needed, because the lambda captures it.
            var boundKey = hotbarKeys[i];
            builder = builder.Bind(boundKey, new PointerInputCmdHandler((in PointerInputCmdArgs args) =>
            {
                if (args.State != BoundKeyState.Down)
                    return false;

                TriggerAction(boundId);
                return true;
            }, false, true));
        }

        // The loadout keys used to flip between action pages. There is only one list now, so they are
        // deliberately left unbound rather than switching to something empty.

        builder
            .Bind(ContentKeyFunctions.OpenActionsMenu,
                InputCmdHandler.FromDelegate(_ => ToggleWindow()))
            .BindBefore(EngineKeyFunctions.Use, new PointerInputCmdHandler(TargetingOnUse, outsidePrediction: true),
                    typeof(ConstructionSystem), typeof(DragDropSystem))
                .BindBefore(EngineKeyFunctions.UIRightClick, new PointerInputCmdHandler(TargetingCancel, outsidePrediction: true))
            .Register<ActionUIController>();
    }

    private bool TargetingCancel(in PointerInputCmdArgs args)
    {
        if (!_timing.IsFirstTimePredicted)
            return false;

        // only do something for actual target-based actions
        if (SelectingTargetFor == null)
            return false;

        StopTargeting();
        return true;
    }

    /// <summary>
    ///     If the user clicked somewhere, and they are currently targeting an action, try and perform it.
    /// </summary>
    private bool TargetingOnUse(in PointerInputCmdArgs args)
    {
        if (!_timing.IsFirstTimePredicted || _actionsSystem == null || SelectingTargetFor is not { } actionId)
            return false;

        if (_playerManager.LocalEntity is not { } user)
            return false;

        if (!EntityManager.TryGetComponent(user, out ActionsComponent? comp))
            return false;

        if (!_actionsSystem.TryGetActionData(actionId, out var baseAction) ||
            baseAction is not BaseTargetActionComponent action)
        {
            return false;
        }

        // Is the action currently valid?
        if (!action.Enabled
            || action is { Charges: 0, RenewCharges: false }
            || action.Cooldown.HasValue && action.Cooldown.Value.End > _timing.CurTime)
        {
            // The user is targeting with this action, but it is not valid. Maybe mark this click as
            // handled and prevent further interactions.
            return !action.InteractOnMiss;
        }

        switch (action)
        {
            case WorldTargetActionComponent mapTarget:
                return TryTargetWorld(args, actionId, mapTarget, user, comp) || !mapTarget.InteractOnMiss;

            case EntityTargetActionComponent entTarget:
                return TryTargetEntity(args, actionId, entTarget, user, comp) || !entTarget.InteractOnMiss;

            case EntityWorldTargetActionComponent entMapTarget:
                return TryTargetEntityWorld(args, actionId, entMapTarget, user, comp) || !entMapTarget.InteractOnMiss;

            default:
                Logger.GetSawmill("action.ui.control").Error($"Unknown targeting action: {actionId.GetType()}");
                return false;
        }
    }

    private bool TryTargetWorld(in PointerInputCmdArgs args, EntityUid actionId, WorldTargetActionComponent action, EntityUid user, ActionsComponent actionComp)
    {
        if (_actionsSystem == null)
            return false;

        var coords = args.Coordinates;

        if (!_actionsSystem.ValidateWorldTarget(user, coords, (actionId, action)))
        {
            // Invalid target.
            if (action.DeselectOnMiss)
                StopTargeting();

            return false;
        }

        if (action.ClientExclusive)
        {
            if (action.Event != null)
            {
                action.Event.Target = coords;
            }

            _actionsSystem.PerformAction(user, actionComp, actionId, action, action.Event, _timing.CurTime);
        }
        else
            EntityManager.RaisePredictiveEvent(new RequestPerformActionEvent(EntityManager.GetNetEntity(actionId), EntityManager.GetNetCoordinates(coords)));

        if (!action.Repeat)
            StopTargeting();

        return true;
    }

    private bool TryTargetEntity(in PointerInputCmdArgs args, EntityUid actionId, EntityTargetActionComponent action, EntityUid user, ActionsComponent actionComp)
    {
        if (_actionsSystem == null)
            return false;

        var entity = args.EntityUid;

        if (!_actionsSystem.ValidateEntityTarget(user, entity, (actionId, action)))
        {
            if (action.DeselectOnMiss)
                StopTargeting();

            return false;
        }

        if (action.ClientExclusive)
        {
            if (action.Event != null)
            {
                action.Event.Target = entity;
            }

            _actionsSystem.PerformAction(user, actionComp, actionId, action, action.Event, _timing.CurTime);
        }
        else
            EntityManager.RaisePredictiveEvent(new RequestPerformActionEvent(EntityManager.GetNetEntity(actionId), EntityManager.GetNetEntity(args.EntityUid)));

        if (!action.Repeat)
            StopTargeting();

        return true;
    }

    private bool TryTargetEntityWorld(in PointerInputCmdArgs args,
        EntityUid actionId,
        EntityWorldTargetActionComponent action,
        EntityUid user,
        ActionsComponent actionComp)
    {
        if (_actionsSystem == null)
            return false;

        var entity = args.EntityUid;
        var coords = args.Coordinates;

        if (!_actionsSystem.ValidateEntityWorldTarget(user, entity, coords, (actionId, action)))
        {
            if (action.DeselectOnMiss)
                StopTargeting();

            return false;
        }

        if (action.ClientExclusive)
        {
            if (action.Event != null)
            {
                action.Event.Entity = entity;
                action.Event.Coords = coords;
            }

            _actionsSystem.PerformAction(user, actionComp, actionId, action, action.Event, _timing.CurTime);
        }
        else
            EntityManager.RaisePredictiveEvent(new RequestPerformActionEvent(EntityManager.GetNetEntity(actionId), EntityManager.GetNetEntity(args.EntityUid), EntityManager.GetNetCoordinates(coords)));

        if (!action.Repeat)
            StopTargeting();

        return true;
    }

    public void UnloadButton()
    {
        if (ActionButton == null)
            return;

        ActionButton.OnPressed -= ActionButtonPressed;
    }

    public void LoadButton()
    {
        if (ActionButton == null)
            return;

        ActionButton.OnPressed += ActionButtonPressed;
    }

    private void OnWindowOpened()
    {
        if (ActionButton != null)
            ActionButton.SetClickPressed(true);

        SearchAndDisplay();
    }

    private void OnWindowClosed()
    {
        if (ActionButton != null)
            ActionButton.SetClickPressed(false);
    }

    public void OnStateExited(GameplayState state)
    {
        if (_actionsSystem != null)
        {
            _actionsSystem.OnActionAdded -= OnActionAdded;
            _actionsSystem.OnActionRemoved -= OnActionRemoved;
            _actionsSystem.ActionsUpdated -= OnActionsUpdated;
        }

        CommandBinds.Unregister<ActionUIController>();
        _pinsByBody.Clear();
    }

    private void TriggerAction(int index)
    {
        if (_actionsSystem == null ||
            index < 0 ||
            index >= _page.Size ||
            _page[index] is not { } actionId ||
            !_actionsSystem.TryGetActionData(actionId, out var baseAction))
            return;

        if (baseAction is BaseTargetActionComponent action)
            ToggleTargeting(actionId, action);
        else
            _actionsSystem?.TriggerAction(actionId, baseAction);
    }

    private void AppendAction(EntityUid action)
    {
        var pins = CurrentPins();
        _page.EnsureSize(MinBarSize());

        for (var i = 0; i < _page.Size; i++)
        {
            if (_page[i] != null || pins?.ContainsKey(i) == true)
                continue;

            _page[i] = action;
            return;
        }

        // No free slot anywhere, so the list simply gets longer.
        _page.Add(action);
    }

    private void OnActionAdded(EntityUid actionId)
    {
        if (_actionsSystem == null ||
            !_actionsSystem.TryGetActionData(actionId, out var action))
            return;

        // if the action is toggled when we add it, start targeting
        if (action is BaseTargetActionComponent targetAction && action.Toggled)
            StartTargeting(actionId, targetAction);

        for (var i = 0; i < _page.Size; i++)
            if (_page[i] == actionId)
                return;

        AppendAction(actionId);
        ApplyPins();
        RefreshBar();
    }

    private void OnActionRemoved(EntityUid actionId)
    {
        if (_container == null)
            return;

        if (actionId == SelectingTargetFor)
            StopTargeting();

        var changed = false;
        for (var i = 0; i < _page.Size; i++)
        {
            if (_page[i] != actionId)
                continue;

            _page[i] = null;
            changed = true;
        }

        // Otherwise a round of picking up and dropping action items leaves the bar trailing columns
        // of empty slots that never go away.
        if (changed)
        {
            _page.TrimEnd(MinBarSize());
            RefreshBar();
        }
    }

    private void OnActionsUpdated()
    {
        QueueWindowUpdate();
        if (_container == null)
            return;

        // TODO ACTIONS allow buttons to persist across state applications
        // Then we don't have to interrupt drags any time the buttons get rebuilt.
        _menuDragHelper.EndDrag();

        ApplyPins();
        RefreshBar();
    }

    private void ActionButtonPressed(ButtonEventArgs args)
    {
        ToggleWindow();
    }

    private void ToggleWindow()
    {
        if (_window == null)
            return;

        if (_window.IsOpen)
        {
            _window.Close();
            return;
        }

        _window.Open();
    }

    private void UpdateFilterLabel()
    {
        if (_window == null)
            return;

        if (_window.FilterButton.SelectedKeys.Count == 0)
        {
            _window.FilterLabel.Visible = false;
        }
        else
        {
            _window.FilterLabel.Visible = true;
            _window.FilterLabel.Text = Loc.GetString("ui-actionmenu-filter-label",
                ("selectedLabels", string.Join(", ", _window.FilterButton.SelectedLabels)));
        }
    }

    private bool MatchesFilter(BaseActionComponent action, Filters filter)
    {
        return filter switch
        {
            Filters.Enabled => action.Enabled,
            Filters.Item => action.Container != null && action.Container != _playerManager.LocalEntity,
            Filters.Innate => action.Container == null || action.Container == _playerManager.LocalEntity,
            Filters.Instant => action is InstantActionComponent,
            Filters.Targeted => action is BaseTargetActionComponent,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null)
        };
    }

    private void ClearList()
    {
        if (_window?.Disposed == false)
            _window.ResultsGrid.RemoveAllChildren();
    }

    private void PopulateActions(IEnumerable<(EntityUid Id, BaseActionComponent Comp)> actions)
    {
        if (_window is not { Disposed: false, IsOpen: true })
            return;

        if (_actionsSystem == null)
            return;

        _window.UpdateNeeded = false;

        List<ActionButton> existing = new(_window.ResultsGrid.ChildCount);
        foreach (var child in _window.ResultsGrid.Children)
        {
            if (child is ActionButton button)
                existing.Add(button);
        }

        var i = 0;
        foreach (var action in actions)
        {
            if (i < existing.Count)
            {
                existing[i++].UpdateData(action.Id, _actionsSystem);
                continue;
            }

            var button = new ActionButton(_entMan, _spriteSystem, this) {Locked = true};
            button.ActionPressed += OnWindowActionPressed;
            button.ActionUnpressed += OnWindowActionUnPressed;
            button.ActionFocusExited += OnWindowActionFocusExisted;
            button.UpdateData(action.Id, _actionsSystem);
            _window.ResultsGrid.AddChild(button);
        }

        for (; i < existing.Count; i++)
        {
            existing[i].Dispose();
        }
    }

    public void QueueWindowUpdate()
    {
        if (_window != null)
            _window.UpdateNeeded = true;
    }

    private void SearchAndDisplay()
    {
        if (_window is not { Disposed: false, IsOpen: true })
            return;

        if (_actionsSystem == null)
            return;

        if (_playerManager.LocalEntity is not { } player)
            return;

        var search = _window.SearchBar.Text;
        var filters = _window.FilterButton.SelectedKeys;
        var actions = _actionsSystem.GetClientActions();

        if (filters.Count == 0 && string.IsNullOrWhiteSpace(search))
        {
            PopulateActions(actions);
            return;
        }

        actions = actions.Where(action =>
        {
            if (filters.Count > 0 && filters.Any(filter => !MatchesFilter(action.Comp, filter)))
                return false;

            if (action.Comp.Keywords.Any(keyword => search.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                return true;

            var name = EntityManager.GetComponent<MetaDataComponent>(action.Id).EntityName;
            if (name.Contains(search, StringComparison.OrdinalIgnoreCase))
                return true;

            if (action.Comp.Container == null || action.Comp.Container == player)
                return false;

            var providerName = EntityManager.GetComponent<MetaDataComponent>(action.Comp.Container.Value).EntityName;
            return providerName.Contains(search, StringComparison.OrdinalIgnoreCase);
        });

        PopulateActions(actions);
    }

    private void SetAction(ActionButton button, EntityUid? actionId)
    {
        if (_actionsSystem == null)
            return;

        int position;

        if (_container != null &&
            _container.TryGetButtonIndex(button, out var slot) &&
            CurrentPins() is { } pins &&
            pins.TryGetValue(slot, out var pin) &&
            (actionId == null || KeyOf(actionId.Value) != pin))
        {
            pins.Remove(slot);
        }

        if (actionId == null)
        {
            button.ClearData();
            if ((_container?.TryGetButtonIndex(button, out position) ?? false)
                && position >= 0
                && position < _page.Size)
            {
                _page[position] = null;
            }
        }
        else if (button.TryReplaceWith(actionId.Value, _actionsSystem) &&
            _container != null &&
            _container.TryGetButtonIndex(button, out position) &&
            position >= 0)
        {
            // The bar always keeps one empty slot past the end as a drop target. Dropping onto it
            // grows the list rather than spilling the action onto a page you have to arrow to.
            _page.EnsureSize(position + 1);
            _page[position] = actionId;
        }
    }

    private void DragAction()
    {
        if (_menuDragHelper.Dragged is not {ActionId: {} action} dragged)
        {
            _menuDragHelper.EndDrag();
            return;
        }

        EntityUid? swapAction = null;
        var currentlyHovered = UIManager.MouseGetControl(_input.MouseScreenPosition);

        // moving a pinned action around the bar takes its pin along
        if (_container != null &&
            CurrentPins() is { } pins &&
            currentlyHovered is ActionButton target &&
            _container.TryGetButtonIndex(dragged, out var from) &&
            _container.TryGetButtonIndex(target, out var to) &&
            from != to)
        {
            var hadFrom = pins.Remove(from, out var fromPin);
            var hadTo = pins.Remove(to, out var toPin);
            if (hadFrom)
                pins[to] = fromPin;
            if (hadTo)
                pins[from] = toPin;
        }

        if (currentlyHovered is ActionButton button)
        {
            swapAction = button.ActionId;
            SetAction(button, action);
        }

        if (dragged.Parent is ActionButtonContainer)
            SetAction(dragged, swapAction);

        RefreshBar();

        _menuDragHelper.EndDrag();
    }

    private void OnClearPressed(ButtonEventArgs args)
    {
        if (_window == null)
            return;

        _window.SearchBar.Clear();
        _window.FilterButton.DeselectAll();
        UpdateFilterLabel();
        QueueWindowUpdate();
    }

    private void OnSearchChanged(LineEditEventArgs args)
    {
        QueueWindowUpdate();
    }

    private void OnFilterSelected(ItemPressedEventArgs args)
    {
        UpdateFilterLabel();
        QueueWindowUpdate();
    }

    private void OnWindowActionPressed(GUIBoundKeyEventArgs args, ActionButton action)
    {
        if (args.Function != EngineKeyFunctions.UIClick && args.Function != EngineKeyFunctions.Use)
            return;

        _menuDragHelper.MouseDown(action);
        args.Handle();
    }

    private void OnWindowActionUnPressed(GUIBoundKeyEventArgs args, ActionButton dragged)
    {
        if (args.Function != EngineKeyFunctions.UIClick && args.Function != EngineKeyFunctions.Use)
            return;

        DragAction();
        args.Handle();
    }

    private void OnWindowActionFocusExisted(ActionButton button)
    {
        _menuDragHelper.EndDrag();
    }

    private void OnActionPressed(GUIBoundKeyEventArgs args, ActionButton button)
    {
        if (args.Function == ContentKeyFunctions.MouseMiddle)
        {
            TogglePin(button);
            args.Handle();
            return;
        }

        if (args.Function == EngineKeyFunctions.UIRightClick)
        {
            SetAction(button, null);
            RefreshBar();
            args.Handle();
            return;
        }

        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        args.Handle();
        if (button.ActionId != null)
        {
            _menuDragHelper.MouseDown(button);
            return;
        }

        var ev = new FillActionSlotEvent();
        EntityManager.EventBus.RaiseEvent(EventSource.Local, ev);
        if (ev.Action != null)
        {
            SetAction(button, ev.Action);
            RefreshBar();
        }
    }

    private void OnActionUnpressed(GUIBoundKeyEventArgs args, ActionButton button)
    {
        if (args.Function != EngineKeyFunctions.UIClick || _actionsSystem == null)
            return;

        args.Handle();

        if (_menuDragHelper.IsDragging)
        {
            DragAction();
            return;
        }

        _menuDragHelper.EndDrag();

        if (!_actionsSystem.TryGetActionData(button.ActionId, out var baseAction))
            return;

        if (baseAction is not BaseTargetActionComponent action)
        {
            _actionsSystem?.TriggerAction(button.ActionId.Value, baseAction);
            return;
        }

        // for target actions, we go into "select target" mode, we don't
        // message the server until we actually pick our target.

        // if we're clicking the same thing we're already targeting for, then we simply cancel
        // targeting
        ToggleTargeting(button.ActionId.Value, action);
    }

    private bool OnMenuBeginDrag()
    {
        // TODO ACTIONS
        // The dragging icon shuld be based on the entity's icon style. I.e. if the action has a large icon texture,
        // and a small item/provider sprite, then the dragged icon should be the big texture, not the provider.
        if (_actionsSystem != null && _actionsSystem.TryGetActionData(_menuDragHelper.Dragged?.ActionId, out var action))
        {
            if (EntityManager.TryGetComponent(action.EntityIcon, out SpriteComponent? sprite)
                && sprite.Icon?.GetFrame(RsiDirection.South, 0) is {} frame)
                _dragShadow.Texture = frame;
            else if (action.Icon != null)
                _dragShadow.Texture = _spriteSystem.Frame0(action.Icon);
            else
                _dragShadow.Texture = null;
        }

        LayoutContainer.SetPosition(_dragShadow, UIManager.MousePositionScaled.Position - new Vector2(32, 32));
        return true;
    }

    private bool OnMenuContinueDrag(float frameTime)
    {
        LayoutContainer.SetPosition(_dragShadow, UIManager.MousePositionScaled.Position - new Vector2(32, 32));
        _dragShadow.Visible = true;
        return true;
    }

    private void OnMenuEndDrag()
    {
        _dragShadow.Texture = null;
        _dragShadow.Visible = false;
    }

    private void UnloadGui()
    {
        _actionsSystem?.UnlinkAllActions();

        if (ActionsBar == null)
            return;

        if (_window != null)
        {
            _window.OnOpen -= OnWindowOpened;
            _window.OnClose -= OnWindowClosed;
            _window.ClearButton.OnPressed -= OnClearPressed;
            _window.SearchBar.OnTextChanged -= OnSearchChanged;
            _window.FilterButton.OnItemSelected -= OnFilterSelected;

            _window.Dispose();
            _window = null;
        }
    }

    private void LoadGui()
    {
        UnloadGui();
        _window = UIManager.CreateWindow<ActionsWindow>();
        LayoutContainer.SetAnchorPreset(_window, LayoutContainer.LayoutPreset.CenterTop);

        _window.OnOpen += OnWindowOpened;
        _window.OnClose += OnWindowClosed;
        _window.ClearButton.OnPressed += OnClearPressed;
        _window.SearchBar.OnTextChanged += OnSearchChanged;
        _window.FilterButton.OnItemSelected += OnFilterSelected;

        if (ActionsBar == null)
            return;

        RegisterActionContainer(ActionsBar.ActionsContainer);

        _actionsSystem?.LinkAllActions();
    }

    public void RegisterActionContainer(ActionButtonContainer container)
    {
        if (_container != null)
        {
            _container.ActionPressed -= OnActionPressed;
            _container.ActionUnpressed -= OnActionUnpressed;
        }

        _container = container;
        _container.ActionPressed += OnActionPressed;
        _container.ActionUnpressed += OnActionUnpressed;
    }

    private void ClearActions()
    {
        _container?.ClearActionData();
    }

    private void AssignSlots(List<SlotAssignment> assignments)
    {
        if (_actionsSystem == null)
            return;

        // Hotbar used to pick which page the slot lived on. There is one list now, so a saved
        // assignment file that still names higher hotbars gets folded onto the end of it.
        var pageStride = ContentKeyFunctions.GetHotbarBoundKeys().Length;

        // Only pages that hold something get a place in the list. Multiplying the raw hotbar number by the
        // stride reserved a full page of blanks for every page the player never filled, so a file saved with a
        // single action on the fifth page unpacked into fifty slots with one action among them.
        var pages = new SortedSet<int>();
        foreach (ref var assignment in CollectionsMarshal.AsSpan(assignments))
        {
            pages.Add(assignment.Hotbar);
        }

        var packed = new Dictionary<int, int>(pages.Count);
        foreach (var hotbar in pages)
        {
            packed[hotbar] = packed.Count;
        }

        foreach (ref var assignment in CollectionsMarshal.AsSpan(assignments))
        {
            var slot = packed[assignment.Hotbar] * pageStride + assignment.Slot;
            _page.EnsureSize(slot + 1);
            _page[slot] = assignment.ActionId;
        }

        // Gaps inside a page are the player's own layout and stay put, but a page that only fills its first few
        // slots must not pad the list out to a full stride behind them.
        _page.TrimEnd(pageStride);

        RefreshBar();
    }

    public void RemoveActionContainer() =>
        _container = null;

    public void OnSystemLoaded(ActionsSystem system)
    {
        system.LinkActions += OnComponentLinked;
        system.UnlinkActions += OnComponentUnlinked;
        system.ClearAssignments += ClearActions;
        system.AssignSlot += AssignSlots;
    }

    public void OnSystemUnloaded(ActionsSystem system)
    {
        system.LinkActions -= OnComponentLinked;
        system.UnlinkActions -= OnComponentUnlinked;
        system.ClearAssignments -= ClearActions;
        system.AssignSlot -= AssignSlots;
    }

    public override void FrameUpdate(FrameEventArgs args)
    {
        _menuDragHelper.Update(args.DeltaSeconds);
        if (_window is {UpdateNeeded: true})
            SearchAndDisplay();
    }

    private void OnComponentLinked(ActionsComponent component)
    {
        if (_actionsSystem == null)
            return;

        LoadDefaultActions();
        RefreshBar();
        QueueWindowUpdate();
    }

    private void OnComponentUnlinked()
    {
        _container?.ClearActionData();
        QueueWindowUpdate();
        StopTargeting();
    }

    private void LoadDefaultActions()
    {
        if (_actionsSystem == null)
            return;

        var actions = _actionsSystem.GetClientActions().Where(action => action.Comp.AutoPopulate).ToList();
        actions.Sort(ActionComparer);

        // Everything goes in one list. Keep at least the keybound slots so 1-0 always have a home.
        // pinned stuff goes in first
        _page.Clear();
        _page.EnsureSize(Math.Max(MinBarSize(), actions.Count));
        ApplyPins();

        foreach (var action in actions)
        {
            if (IndexOf(action.Id) < 0)
                AppendAction(action.Id);
        }

        _page.TrimEnd(MinBarSize());
    }

    private void RefreshBar()
    {
        if (_container == null || _actionsSystem == null)
            return;

        _container.SetActionData(_actionsSystem, _page);

        var pins = CurrentPins();
        var i = 0;
        foreach (var button in _container.GetButtons())
        {
            button.SetPinned(pins?.ContainsKey(i) == true);
            i++;
        }
    }

    private Dictionary<int, PinKey>? CurrentPins(bool create = false)
    {
        if (_playerManager.LocalEntity is not { } body || EntityManager.HasComponent<GhostComponent>(body))
            return null;

        if (_pinsByBody.TryGetValue(body, out var pins))
            return pins;

        if (!create)
            return null;

        pins = new Dictionary<int, PinKey>();
        _pinsByBody[body] = pins;
        return pins;
    }

    private int MinBarSize()
    {
        var size = ContentKeyFunctions.GetHotbarBoundKeys().Length;
        if (CurrentPins() is { } pins)
        {
            foreach (var slot in pins.Keys)
                size = Math.Max(size, slot + 1);
        }

        return size;
    }

    private int IndexOf(EntityUid action)
    {
        for (var i = 0; i < _page.Size; i++)
        {
            if (_page[i] == action)
                return i;
        }

        return -1;
    }

    // same action from the same kind of item counts as the same pin
    private PinKey? KeyOf(EntityUid actionId)
    {
        if (_actionsSystem == null || !_actionsSystem.TryGetActionData(actionId, out var action))
            return null;

        if (EntityManager.GetComponentOrNull<MetaDataComponent>(actionId)?.EntityPrototype?.ID is not { } proto)
            return null;

        string? provider = null;
        if (action.Container is { } container && container != _playerManager.LocalEntity)
            provider = EntityManager.GetComponentOrNull<MetaDataComponent>(container)?.EntityPrototype?.ID;

        return new PinKey(proto, provider);
    }

    private void TogglePin(ActionButton button)
    {
        if (_container == null || !_container.TryGetButtonIndex(button, out var slot))
            return;

        if (CurrentPins(true) is not { } pins)
            return;

        if (pins.Remove(slot))
        {
            _page.TrimEnd(MinBarSize());
            RefreshBar();
            return;
        }

        if (button.ActionId is not { } actionId || pins.Count >= MaxPins || KeyOf(actionId) is not { } key)
            return;

        _page.EnsureSize(slot + 1);
        pins[slot] = key;
        RefreshBar();
    }

    // puts every pinned action that we actually have back into its slot
    private void ApplyPins()
    {
        if (_actionsSystem == null || CurrentPins() is not { Count: > 0 } pins)
            return;

        _page.EnsureSize(MinBarSize());
        _pinClaimed.Clear();

        foreach (var (slot, key) in pins)
        {
            if (_page[slot] is { } current && KeyOf(current) == key)
                _pinClaimed.Add(current);
        }

        foreach (var slot in pins.Keys.OrderBy(x => x))
        {
            var key = pins[slot];
            var occupant = _page[slot];
            if (occupant != null && KeyOf(occupant.Value) == key)
                continue;

            EntityUid? found = null;
            foreach (var (id, _) in _actionsSystem.GetClientActions())
            {
                if (_pinClaimed.Contains(id) || KeyOf(id) != key)
                    continue;

                found = id;
                break;
            }

            if (found is not { } action)
                continue;

            _pinClaimed.Add(action);
            var old = IndexOf(action);
            if (old >= 0)
                _page[old] = null;

            _page[slot] = action;

            if (occupant == null)
                continue;

            if (old >= 0 && !pins.ContainsKey(old))
                _page[old] = occupant;
            else
                AppendAction(occupant.Value);
        }
    }

    /// <summary>
    /// If currently targeting with this slot, stops targeting.
    /// If currently targeting with no slot or a different slot, switches to
    /// targeting with the specified slot.
    /// </summary>
    private void ToggleTargeting(EntityUid actionId, BaseTargetActionComponent action)
    {
        if (SelectingTargetFor == actionId)
        {
            StopTargeting();
            return;
        }

        StartTargeting(actionId, action);
    }

    /// <summary>
    /// Puts us in targeting mode, where we need to pick either a target point or entity
    /// </summary>
    private void StartTargeting(EntityUid actionId, BaseTargetActionComponent action)
    {
        // If we were targeting something else we should stop
        StopTargeting();

        SelectingTargetFor = actionId;
        // TODO inform the server
        action.Toggled = true;

        // override "held-item" overlay
        var provider = action.Container;

        if (action.TargetingIndicator && _overlays.TryGetOverlay<ShowHandItemOverlay>(out var handOverlay))
        {
            if (action.ItemIconStyle == ItemActionIconStyle.BigItem && action.Container != null)
            {
                handOverlay.EntityOverride = provider;
            }
            else if (action.Toggled && action.IconOn != null)
                handOverlay.IconOverride = _spriteSystem.Frame0(action.IconOn);
            else if (action.Icon != null)
                handOverlay.IconOverride = _spriteSystem.Frame0(action.Icon);
        }

        if (_container != null)
        {
            foreach (var button in _container.GetButtons())
            {
                if (button.ActionId == actionId)
                    button.UpdateIcons();
            }
        }

        // TODO: allow world-targets to check valid positions. E.g., maybe:
        // - Draw a red/green ghost entity
        // - Add a yes/no checkmark where the HandItemOverlay usually is

        // Highlight valid entity targets
        if (action is not EntityTargetActionComponent entityAction)
            return;

        Func<EntityUid, bool>? predicate = null;
        var attachedEnt = entityAction.AttachedEntity;

        if (!entityAction.CanTargetSelf)
            predicate = e => e != attachedEnt;

        var range = entityAction.CheckCanAccess ? action.Range : -1;

        _interactionOutline?.SetEnabled(false);
        _targetOutline?.Enable(range, entityAction.CheckCanAccess, predicate, entityAction.Whitelist, null);
    }

    /// <summary>
    /// Switch out of targeting mode if currently selecting target for an action
    /// </summary>
    private void StopTargeting()
    {
        if (SelectingTargetFor == null)
            return;

        var oldAction = SelectingTargetFor;
        if (_actionsSystem != null && _actionsSystem.TryGetActionData(oldAction, out var action))
        {
            // TODO inform the server
            action.Toggled = false;
        }

        SelectingTargetFor = null;

        _targetOutline?.Disable();
        _interactionOutline?.SetEnabled(true);

        if (_container != null)
        {
            foreach (var button in _container.GetButtons())
            {
                if (button.ActionId == oldAction)
                    button.UpdateIcons();
            }
        }

        if (!_overlays.TryGetOverlay<ShowHandItemOverlay>(out var handOverlay))
            return;

        handOverlay.IconOverride = null;
        handOverlay.EntityOverride = null;
    }

    private record struct PinKey(string Action, string? Provider);

    /// <summary>
    /// The player's action slots. Grows as actions are added instead of overflowing onto a second page.
    /// </summary>
    //TODO: Serialize this shit
    private sealed class ActionPage(int size)
    {
        private readonly List<EntityUid?> _data = new(Enumerable.Repeat((EntityUid?) null, size));

        public EntityUid? this[int index]
        {
            get => _data[index];
            set => _data[index] = value;
        }

        public static implicit operator EntityUid?[](ActionPage p) => p._data.ToArray();

        public void Clear()
        {
            for (var i = 0; i < _data.Count; i++)
                _data[i] = null;
        }

        public void Add(EntityUid action) => _data.Add(action);

        /// <summary>
        /// Pads the list out with empty slots so <paramref name="size"/> indices are addressable.
        /// Never shrinks, so slots a player has dragged an action into survive a repopulate.
        /// </summary>
        public void EnsureSize(int size)
        {
            while (_data.Count < size)
                _data.Add(null);
        }

        /// <summary>
        /// Drops empty slots off the end, down to <paramref name="minSize"/>.
        /// </summary>
        public void TrimEnd(int minSize)
        {
            while (_data.Count > minSize && _data[^1] == null)
                _data.RemoveAt(_data.Count - 1);
        }

        public int Size => _data.Count;
    }
}
