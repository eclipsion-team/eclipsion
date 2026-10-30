using System.Linq;
using Content.Shared.Popups;
using Content.Shared.Verbs;

namespace Content.Shared.DeltaV.Paper;

/// <summary>
/// Adds verbs for picking a signature font and color on pens that have more than one.
/// </summary>
public sealed class SignatureWriterSystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private static readonly VerbCategory FontSelect = new("verb-categories-signature-font-select");
    private static readonly VerbCategory ColorSelect = new("verb-categories-signature-color-select");

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SignatureWriterComponent, ComponentInit>(OnCompInit);
        // Plain verbs rather than alternative ones, so alt-clicking the pen doesn't silently change the selection.
        SubscribeLocalEvent<SignatureWriterComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
    }

    private void OnCompInit(Entity<SignatureWriterComponent> ent, ref ComponentInit args)
    {
        if (ent.Comp.ColorList.Count >= 1)
            ent.Comp.Color = ent.Comp.ColorList.First().Value;
    }

    private void OnGetVerbs(Entity<SignatureWriterComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;

        if (ent.Comp.FontList.Count >= 2)
        {
            var priority = 0;
            foreach (var (name, font) in ent.Comp.FontList)
            {
                args.Verbs.Add(new Verb
                {
                    Text = name,
                    Category = FontSelect,
                    Priority = priority--,
                    Act = () =>
                    {
                        ent.Comp.Font = font;
                        _popup.PopupClient(Loc.GetString("signature-writer-component-font-set", ("font", name)), user, user);
                    }
                });
            }
        }

        if (ent.Comp.ColorList.Count >= 2)
        {
            var priority = 0;
            foreach (var (name, color) in ent.Comp.ColorList)
            {
                args.Verbs.Add(new Verb
                {
                    Text = name,
                    Category = ColorSelect,
                    Priority = priority--,
                    Act = () =>
                    {
                        ent.Comp.Color = color;
                        _popup.PopupClient(Loc.GetString("signature-writer-component-color-set", ("color", name)), user, user);
                    }
                });
            }
        }
    }
}
