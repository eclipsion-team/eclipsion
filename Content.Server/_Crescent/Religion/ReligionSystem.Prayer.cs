using Content.Server._Crescent.Religion.Components;
using Content.Shared._Crescent.Religion;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Crescent.Religion;

/// <summary>
///     Believers who touch their own faith's altar kneel and whisper a short prayer.
/// </summary>
public sealed partial class ReligionSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;

    private void InitializePrayer()
    {
        SubscribeLocalEvent<ReligionAltarComponent, InteractHandEvent>(OnAltarInteractHand);
        SubscribeLocalEvent<ReligionAltarComponent, ReligionPrayerDoAfterEvent>(OnPrayerStep);
    }

    private void OnAltarInteractHand(Entity<ReligionAltarComponent> ent, ref InteractHandEvent args)
    {
        if (args.Handled
            || !HasComp<HumanoidAppearanceComponent>(args.User)
            || !_proto.TryIndex(ent.Comp.Religion, out var religion)
            || religion.PrayerLines.Count == 0
            || !HoldsReligion(args.User, religion.ID))
            return;

        args.Handled = true;

        // Touching the altar again mid-prayer cancels the duplicate do-after, which ends the prayer.
        if (!StartPrayerStep(args.User, ent, religion, 0))
            return;

        _popup.PopupEntity(Loc.GetString("religion-prayer-begin"), args.User, args.User, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("religion-prayer-begin-others", ("user", IdentityName(args.User))),
            args.User,
            Filter.PvsExcept(args.User),
            true);

        WhisperPrayer(args.User, religion);
    }

    private bool StartPrayerStep(EntityUid user, Entity<ReligionAltarComponent> altar, ReligionPrototype religion, int step)
    {
        var args = new DoAfterArgs(EntityManager,
            user,
            religion.PrayerStepDelay,
            new ReligionPrayerDoAfterEvent(step),
            altar,
            target: altar)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            DistanceThreshold = altar.Comp.RiteRange,
        };

        return _doAfter.TryStartDoAfter(args);
    }

    private void OnPrayerStep(Entity<ReligionAltarComponent> ent, ref ReligionPrayerDoAfterEvent args)
    {
        if (args.Handled || !_proto.TryIndex(ent.Comp.Religion, out var religion))
            return;

        args.Handled = true;
        var user = args.User;

        if (args.Cancelled)
        {
            _popup.PopupEntity(Loc.GetString("religion-prayer-interrupted"), user, user, PopupType.Small);
            return;
        }

        if (!HoldsReligion(user, religion.ID))
            return;

        var next = args.Step + 1;
        if (next < religion.PrayerSteps)
        {
            if (StartPrayerStep(user, ent, religion, next))
                WhisperPrayer(user, religion);

            return;
        }

        _popup.PopupEntity(Loc.GetString("religion-prayer-finished"), user, user, PopupType.Small);
    }

    private void WhisperPrayer(EntityUid user, ReligionPrototype religion)
    {
        SpeakRiteLine(user, NextPrayerLine(user, religion), InGameICChatType.Whisper);
    }

    /// <summary>
    ///     Draws from a shuffled bag of the faith's prayer lines, so nobody repeats a line before saying all of them.
    /// </summary>
    private LocId NextPrayerLine(EntityUid user, ReligionPrototype religion)
    {
        var believer = EnsureComp<BelieverComponent>(user);

        if (believer.PrayerBag.Count == 0 || believer.PrayerBagReligion != religion.ID)
        {
            believer.PrayerBag.Clear();
            believer.PrayerBag.AddRange(religion.PrayerLines);
            _random.Shuffle(believer.PrayerBag);
            believer.PrayerBagReligion = religion.ID;

            var last = believer.PrayerBag.Count - 1;
            if (last > 0 && believer.PrayerBag[last] == believer.LastPrayer)
                (believer.PrayerBag[0], believer.PrayerBag[last]) = (believer.PrayerBag[last], believer.PrayerBag[0]);
        }

        var line = believer.PrayerBag[^1];
        believer.PrayerBag.RemoveAt(believer.PrayerBag.Count - 1);
        believer.LastPrayer = line;
        return line;
    }
}
