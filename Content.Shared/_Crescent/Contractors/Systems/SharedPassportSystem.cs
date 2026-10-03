using Content.Shared._Crescent.Contractors.Components;
using Content.Shared._Crescent.Contractors.Prototypes;
using Content.Shared.Administration.Logs;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Clothing.Loadouts.Systems;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.PDA;
using Content.Shared.Stacks;
using Content.Shared.Preferences;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.UserInterface;
using Robust.Shared;
using Content.Shared.CCVar;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;


namespace Content.Shared._Crescent.Contractors.Systems;

public class SharedPassportSystem : EntitySystem
{
    public const int CurrentYear = 2450;
    public const int PassportLifetimeYears = 5;
    private const int MaxTextFieldLength = 64;
    private const string PIDChars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _sharedTransformSystem = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogManager = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PassportComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<PlayerLoadoutAppliedEvent>(OnPlayerLoadoutApplied);
        SubscribeLocalEvent<PassportComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<PassportComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<PassportComponent, PassportSaveMessage>(OnSave);
    }

    private void OnExamined(EntityUid uid, PassportComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange || component.IsClosed)
            return;

        args.PushMarkup(Loc.GetString("passport-registered-to", ("name", DisplayOrUnspecified(component.FullName))), 60);
        args.PushMarkup(Loc.GetString("passport-age", ("age", component.Age)), 59);
        args.PushMarkup(Loc.GetString("passport-species", ("species", DisplayOrUnspecified(component.Species))), 58);
        args.PushMarkup(Loc.GetString("passport-gender", ("gender", DisplayOrUnspecified(component.Sex))), 57);
        args.PushMarkup(Loc.GetString("passport-height", ("height", component.HeightCm)), 56);
        args.PushMarkup(Loc.GetString("passport-nationality", ("nationality", DisplayOrUnspecified(component.Nationality))), 53);
        args.PushMarkup(Loc.GetString("passport-employer", ("employer", DisplayOrUnspecified(component.Employer))), 52);
        args.PushMarkup(Loc.GetString("passport-lifepath", ("lifepath", DisplayOrUnspecified(component.Lifepath))), 51);
        args.PushMarkup(Loc.GetString("passport-year-of-birth", ("year", CurrentYear - component.Age)), 49);
        args.PushMarkup(Loc.GetString("passport-issued", ("year", component.IssueYear)), 48);
        args.PushMarkup(Loc.GetString("passport-expires", ("year", component.ExpirationYear)), 47);
        args.PushMarkup(Loc.GetString("passport-pid", ("pid", DisplayOrUnspecified(component.PassportId))), 46);
    }

    private void OnPlayerLoadoutApplied(PlayerLoadoutAppliedEvent ev) =>
        SpawnPassportForPlayer(ev.Mob, ev.Profile, ev.JobId);

    public void SpawnPassportForPlayer(EntityUid mob, HumanoidCharacterProfile profile, string? jobId)
    {
        if (jobId == null || !_prototypeManager.TryIndex(
                jobId,
                out JobPrototype? jobPrototype)
            || !jobPrototype.CanHavePassport
            || Deleted(mob)
            || !Exists(mob)
            || !ShouldSpawnPassports)
            return;

        if (!_prototypeManager.TryIndex(
            profile.Nationality,
            out NationalityPrototype? nationalityPrototype) || !_prototypeManager.TryIndex(nationalityPrototype.PassportPrototype, out EntityPrototype? entityPrototype))
            return;

        var passportEntity = _entityManager.SpawnEntity(entityPrototype.ID, _sharedTransformSystem.GetMapCoordinates(mob));
        var passportComponent = _entityManager.GetComponent<PassportComponent>(passportEntity);

        UpdatePassportProfile(new(passportEntity, passportComponent), profile);

        // The document belongs in the PDA's passport pocket. Only when there is no PDA, or its
        // pocket is already taken, does it fall back to the backpack and then to the floor.
        if (TryInsertIntoPda(mob, passportEntity))
            return;

        // Try to find back-mounted storage apparatus
        if (_inventory.TryGetSlotEntity(mob, "back", out var item) &&
                EntityManager.TryGetComponent<StorageComponent>(item, out var inventory))
        // Try inserting the entity into the storage, if it can't, it leaves the loadout item on the ground
        {
            if (!EntityManager.TryGetComponent<ItemComponent>(passportEntity, out var itemComp)
                || !_storage.CanInsert(item.Value, passportEntity, out _, inventory, itemComp)
                || !_storage.Insert(item.Value, passportEntity, out _, playSound: false))
            {
                _adminLogManager.Add(
                    LogType.EntitySpawn,
                    LogImpact.Low,
                    $"Passport for {profile.Name} was spawned on the floor due to missing bag space");
            }
        }
    }

    /// <summary>
    /// Slots a PDA is normally worn in. A loose PDA in a hand or a bag is deliberately not hunted
    /// down: the passport just falls through to the backpack in that case.
    /// </summary>
    private static readonly string[] PdaSlots = { "id", "belt" };

    private bool TryInsertIntoPda(EntityUid mob, EntityUid passport)
    {
        foreach (var slot in PdaSlots)
        {
            if (!_inventory.TryGetSlotEntity(mob, slot, out var worn)
                || !HasComp<PdaComponent>(worn.Value))
                continue;

            if (_itemSlots.TryInsert(worn.Value, PdaComponent.PdaPassportSlotId, passport, null))
                return true;
        }

        return false;
    }

    private bool ShouldSpawnPassports =>
        _configManager.GetCVar(Content.Shared.CCVar.CCVars.ContractorsEnabled) &&
        _configManager.GetCVar(Content.Shared.CCVar.CCVars.ContractorsPassportEnabled);

    public void UpdatePassportProfile(Entity<PassportComponent> passport, HumanoidCharacterProfile profile)
    {
        var species = _prototypeManager.Index<SpeciesPrototype>(profile.Species);
        var nationality = _prototypeManager.TryIndex(profile.Nationality, out NationalityPrototype? nationalityPrototype)
            ? Loc.GetString(nationalityPrototype.NameKey)
            : profile.Nationality;
        var employer = _prototypeManager.TryIndex(profile.Employer, out EmployerPrototype? employerPrototype)
            ? Loc.GetString(employerPrototype.NameKey)
            : profile.Employer;
        var lifepath = _prototypeManager.TryIndex(profile.Lifepath, out LifepathPrototype? lifepathPrototype)
            ? Loc.GetString(lifepathPrototype.NameKey)
            : profile.Lifepath;

        passport.Comp.FullName = profile.Name;
        passport.Comp.Age = profile.Age;
        passport.Comp.Species = string.IsNullOrWhiteSpace(profile.Customspeciename)
            ? Loc.GetString(species.Name)
            : profile.Customspeciename;
        passport.Comp.PortraitSpecies = profile.Species;
        passport.Comp.Sex = profile.Sex.ToString();
        passport.Comp.HeightCm = (int) MathF.Round(profile.Height * species.AverageHeight);
        passport.Comp.Nationality = nationality;
        passport.Comp.Employer = employer;
        passport.Comp.Lifepath = lifepath;
        passport.Comp.IssueYear = CurrentYear;
        passport.Comp.ExpirationYear = CurrentYear + PassportLifetimeYears;
        passport.Comp.IsClosed = true;
        passport.Comp.PassportId = GenerateIdentityString(profile.Name
            + profile.Species
            + profile.Height
            + profile.Age
            + profile.Nationality
            + profile.FlavorText);

        // The registry copy is taken here, once, from the data the issuer put on the document.
        // Everything after this point is the holder's business: the editor can rewrite every
        // printed field but has no path to this snapshot, which is what makes a forgery findable.
        passport.Comp.Record = passport.Comp.Authentic
            ? new PassportRecord
            {
                FullName = passport.Comp.FullName,
                Age = passport.Comp.Age,
                Species = passport.Comp.Species,
                Sex = passport.Comp.Sex,
                HeightCm = passport.Comp.HeightCm,
                Nationality = passport.Comp.Nationality,
                Employer = passport.Comp.Employer,
                Lifepath = passport.Comp.Lifepath,
                PassportId = passport.Comp.PassportId,
                IssueYear = passport.Comp.IssueYear,
                ExpirationYear = passport.Comp.ExpirationYear,
            }
            : null;

        Dirty(passport);
    }

    private void OnUiOpened(Entity<PassportComponent> passport, ref BoundUIOpenedEvent args)
    {
        if (args.UiKey is PassportUiKey.Key)
            UpdateUiState(passport);
    }

    private void OnSave(Entity<PassportComponent> passport, ref PassportSaveMessage args)
    {
        var fullName = Clean(args.FullName);
        var age = Math.Clamp(args.Age, 0, 1000);
        var species = Clean(args.Species);
        var sex = Clean(args.Sex, 32);
        var heightCm = Math.Clamp(args.HeightCm, 0, 1000);
        var nationality = Clean(args.Nationality);
        var employer = Clean(args.Employer);
        var lifepath = Clean(args.Lifepath);
        var passportId = Clean(args.PassportId, 32).ToUpperInvariant();
        var issueYear = Math.Clamp(args.IssueYear, 0, 9999);
        var expirationYear = Math.Clamp(args.ExpirationYear, 0, 9999);

        // The cover is the one field the editor cannot rewrite for free. Rebinding the document
        // in another polity's binding costs a piece of cloth and nothing else, and it never
        // touches the issuer's record, so the forgery stays findable by a checker printout.
        var cover = passport.Comp.Cover;
        var rebindDenied = false;

        if (args.Cover != cover.Id
            && _prototypeManager.TryIndex(args.Cover, out PassportCoverPrototype? requested)
            && requested.Selectable)
        {
            if (TryConsumeRebindMaterial(passport, args.Actor))
                cover = args.Cover;
            else
                rebindDenied = true;
        }

        var changed = passport.Comp.Cover != cover
            || passport.Comp.FullName != fullName
            || passport.Comp.Age != age
            || passport.Comp.Species != species
            || passport.Comp.Sex != sex
            || passport.Comp.HeightCm != heightCm
            || passport.Comp.Nationality != nationality
            || passport.Comp.Employer != employer
            || passport.Comp.Lifepath != lifepath
            || passport.Comp.PassportId != passportId
            || passport.Comp.IssueYear != issueYear
            || passport.Comp.ExpirationYear != expirationYear;

        if (changed)
        {
            passport.Comp.Cover = cover;
            passport.Comp.FullName = fullName;
            passport.Comp.Age = age;
            passport.Comp.Species = species;
            passport.Comp.Sex = sex;
            passport.Comp.HeightCm = heightCm;
            passport.Comp.Nationality = nationality;
            passport.Comp.Employer = employer;
            passport.Comp.Lifepath = lifepath;
            passport.Comp.PassportId = passportId;
            passport.Comp.IssueYear = issueYear;
            passport.Comp.ExpirationYear = expirationYear;
            Dirty(passport);
        }

        UpdateUiState(passport);
        _popup.PopupPredicted(
            Loc.GetString(rebindDenied ? "passport-rebind-no-material" : "passport-edit-saved"),
            passport,
            args.Actor);
    }

    /// <summary>
    /// Spends the rebinding cost out of a stack the actor is holding. Held only, so a rebind is
    /// always a deliberate two-handed act rather than something a pocket pays for silently.
    /// </summary>
    private bool TryConsumeRebindMaterial(Entity<PassportComponent> passport, EntityUid actor)
    {
        if (passport.Comp.RebindCost <= 0)
            return true;

        foreach (var held in _hands.EnumerateHeld(actor))
        {
            if (held == passport.Owner
                || !TryComp<StackComponent>(held, out var stack)
                || stack.StackTypeId != passport.Comp.RebindMaterial.Id
                || stack.Count < passport.Comp.RebindCost)
                continue;

            return _stack.Use(held, passport.Comp.RebindCost, stack);
        }

        return false;
    }

    private void UpdateUiState(Entity<PassportComponent> passport)
    {
        var component = passport.Comp;
        _ui.SetUiState(passport.Owner, PassportUiKey.Key, new PassportBoundUserInterfaceState(
            component.Cover,
            component.FullName,
            component.Age,
            component.Species,
            component.Sex,
            component.HeightCm,
            component.Nationality,
            component.Employer,
            component.Lifepath,
            component.PassportId,
            component.IssueYear,
            component.ExpirationYear));
    }

    private void OnUseInHand(Entity<PassportComponent> passport, ref UseInHandEvent evt)
    {
        // Deliberately not gated on IsFirstTimePredicted. Every prediction replay re-runs this
        // from the last server state, so skipping the replays let the client snap back to the
        // old sprite for the rest of the round trip - the flicker seen when leafing through it.
        if (evt.Handled)
            return;

        evt.Handled = true;
        passport.Comp.IsClosed = !passport.Comp.IsClosed;
        Dirty(passport);

        var passportEvent = new PassportToggleEvent();
        RaiseLocalEvent(passport, ref passportEvent);
    }

    private static string GenerateIdentityString(string seed)
    {
        var hashCode = seed.GetHashCode();
        System.Random random = new System.Random(hashCode);

        char[] result = new char[17]; // 15 characters + 2 dashes

        int j = 0;
        for (int i = 0; i < 15; i++)
        {
            if (i == 5 || i == 10)
            {
                result[j++] = '-';
            }
            result[j++] = PIDChars[random.Next(PIDChars.Length)];
        }

        return new string(result);
    }

    private static string Clean(string value, int maxLength = MaxTextFieldLength)
    {
        var cleaned = value.Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private string DisplayOrUnspecified(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Loc.GetString("passport-unspecified")
            : FormattedMessage.EscapeText(value);
    }

    [ByRefEvent]
    public sealed class PassportToggleEvent : HandledEntityEventArgs { }

}
