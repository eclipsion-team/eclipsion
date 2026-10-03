using Content.Shared._Crescent.Religion;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Robust.Shared.Enums;
using Robust.Shared.Maths;

namespace Content.Shared.Roles;

/// <summary>
/// Replaces a player's in-round humanoid identity for a job without modifying their saved character.
/// Account-scoped data such as bank balance, traits, loadouts, and character flags is preserved.
/// </summary>
[DataDefinition]
public sealed partial class JobCharacterOverride
{
    [DataField(required: true)]
    public string Name { get; private set; } = string.Empty;

    [DataField]
    public string FlavorText { get; private set; } = string.Empty;

    [DataField]
    public string Species { get; private set; } = SharedHumanoidAppearanceSystem.DefaultSpecies;

    [DataField]
    public int Age { get; private set; } = 18;

    [DataField]
    public Sex Sex { get; private set; } = Sex.Male;

    [DataField]
    public Gender Gender { get; private set; } = Gender.Male;

    [DataField]
    public float Height { get; private set; } = 1f;

    [DataField]
    public float Width { get; private set; } = 1f;

    [DataField]
    public string HairStyle { get; private set; } = HairStyles.DefaultHairStyle;

    [DataField]
    public Color HairColor { get; private set; } = Color.Black;

    [DataField]
    public string FacialHairStyle { get; private set; } = HairStyles.DefaultFacialHairStyle;

    [DataField]
    public Color FacialHairColor { get; private set; } = Color.Black;

    [DataField]
    public Color EyeColor { get; private set; } = Color.Black;

    /// <summary>
    /// Human skin tone on the standard 0 (light) to 100 (dark) scale.
    /// </summary>
    [DataField]
    public int HumanSkinTone { get; private set; } = 20;

    [DataField]
    public string Nationality { get; private set; } = SharedHumanoidAppearanceSystem.DefaultNationality;

    [DataField]
    public string Employer { get; private set; } = SharedHumanoidAppearanceSystem.DefaultEmployer;

    [DataField]
    public string Lifepath { get; private set; } = SharedHumanoidAppearanceSystem.DefaultLifepath;

    [DataField]
    public string Religion { get; private set; } = ReligionPrototype.Default;

    [DataField]
    public string Faction { get; private set; } = string.Empty;

    [DataField]
    public string Subfaction { get; private set; } = string.Empty;

    public HumanoidCharacterProfile ApplyTo(HumanoidCharacterProfile profile)
    {
        var appearance = new HumanoidCharacterAppearance(
            HairStyle,
            HairColor,
            FacialHairStyle,
            FacialHairColor,
            EyeColor,
            SkinColor.HumanSkinTone(HumanSkinTone),
            []);

        return profile
            .WithName(Name)
            .WithFlavorText(FlavorText)
            .WithSpecies(Species)
            .WithCustomSpeciesName(string.Empty)
            .WithAge(Age)
            .WithSex(Sex)
            .WithGender(Gender)
            .WithDisplayPronouns(null)
            .WithStationAiName(null)
            .WithCyborgName(null)
            .WithHeight(Height)
            .WithWidth(Width)
            .WithCharacterAppearance(appearance)
            .WithNationality(Nationality)
            .WithEmployer(Employer)
            .WithLifepath(Lifepath)
            .WithReligion(Religion)
            .WithFaction(Faction)
            .WithSubfaction(Subfaction);
    }
}
