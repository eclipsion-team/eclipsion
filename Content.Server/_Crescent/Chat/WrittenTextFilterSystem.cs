using System.Collections;
using System.Linq;
using System.Reflection;
using Content.Server.Chat.Managers;
using Content.Server.Popups;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Crescent.Chat;

public sealed class WrittenTextFilterSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PopupSystem _popup = default!;

    private const int MaxDepth = 3;

    private const int MaxIdLength = 128;

    private readonly Dictionary<Type, Func<object, object?>[]> _readers = new();
    private readonly List<string> _texts = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BoundUserInterfaceMessageAttempt>(OnMessageAttempt);
    }

    private void OnMessageAttempt(BoundUserInterfaceMessageAttempt args)
    {
        if (args.Cancelled || !TryComp<ActorComponent>(args.Actor, out var actor))
            return;

        _texts.Clear();
        Collect(args.Message, 0);

        foreach (var text in _texts)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                IsPrototypeId(text) ||
                !_chat.IsMessageFiltered(actor.PlayerSession, text, args.Message.GetType().Name))
            {
                continue;
            }

            _popup.PopupEntity(Loc.GetString("chat-filter-blocked-writing"), args.Actor, args.Actor, PopupType.MediumCaution);
            args.Cancel();
            break;
        }
    }

    private bool IsPrototypeId(string text)
    {
        if (text.Length > MaxIdLength || text.Any(char.IsWhiteSpace))
            return false;

        foreach (var kind in _proto.EnumeratePrototypeKinds())
        {
            if (_proto.TryIndex(kind, text, out _))
                return true;
        }

        return false;
    }

    private void Collect(object message, int depth)
    {
        foreach (var read in GetReaders(message.GetType()))
        {
            object? value;
            try
            {
                value = read(message);
            }
            catch (Exception)
            {
                continue;
            }

            switch (value)
            {
                case null:
                    break;
                case string text:
                    _texts.Add(text);
                    break;
                case IEnumerable<string> texts:
                    _texts.AddRange(texts);
                    break;
                case IEnumerable:
                    // lists of anything else are ids, numbers or coordinates
                    break;
                default:
                    if (depth < MaxDepth && Descends(value.GetType()))
                        Collect(value, depth + 1);
                    break;
            }
        }
    }

    private Func<object, object?>[] GetReaders(Type type)
    {
        if (_readers.TryGetValue(type, out var readers))
            return readers;

        var list = new List<Func<object, object?>>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        foreach (var field in type.GetFields(flags))
        {
            if (MayHoldText(field.FieldType))
                list.Add(field.GetValue);
        }

        foreach (var property in type.GetProperties(flags))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0 && MayHoldText(property.PropertyType))
                list.Add(property.GetValue);
        }

        readers = list.ToArray();
        _readers[type] = readers;
        return readers;
    }

    private static bool MayHoldText(Type type)
    {
        return type == typeof(string) ||
               typeof(IEnumerable<string>).IsAssignableFrom(type) ||
               type == typeof(object) ||
               Descends(type);
    }

    private static bool Descends(Type type)
    {
        return type.IsClass &&
               type != typeof(string) &&
               !typeof(IEnumerable).IsAssignableFrom(type) &&
               type.Namespace?.StartsWith("Content.", StringComparison.Ordinal) == true;
    }
}
