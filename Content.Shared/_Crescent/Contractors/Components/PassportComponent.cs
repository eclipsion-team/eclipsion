using Content.Shared._Crescent.Contractors.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Crescent.Contractors.Components;

/// <summary>
/// Structured identity data printed in a passport. These values deliberately live on the
/// document instead of continuing to reference the character profile so the document can be
/// edited independently in-game.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PassportComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool IsClosed = true;

    [DataField, AutoNetworkedField]
    public string FullName = string.Empty;

    [DataField, AutoNetworkedField]
    public int Age;

    [DataField, AutoNetworkedField]
    public string Species = string.Empty;

    /// <summary>
    /// Species used by the portrait sprite. This is separate from the editable printed species
    /// so arbitrary player text cannot request a sprite state that does not exist.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string PortraitSpecies = "human";

    /// <summary>
    /// The binding this document is printed in. Purely cosmetic and freely rebindable, so it can
    /// disagree with <see cref="Nationality"/> and with the issuer's <see cref="Record"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public ProtoId<PassportCoverPrototype> Cover = "PassportCoverBiesel";

    [DataField, AutoNetworkedField]
    public string Sex = string.Empty;

    [DataField, AutoNetworkedField]
    public int HeightCm;

    [DataField, AutoNetworkedField]
    public string Nationality = string.Empty;

    [DataField, AutoNetworkedField]
    public string Employer = string.Empty;

    [DataField, AutoNetworkedField]
    public string Lifepath = string.Empty;

    [DataField, AutoNetworkedField]
    public string PassportId = string.Empty;

    [DataField, AutoNetworkedField]
    public int IssueYear;

    [DataField, AutoNetworkedField]
    public int ExpirationYear;

    /// <summary>
    /// Stack consumed to rebind the document in another polity's cover. Nothing else is required:
    /// rebinding is meant to be cheap, and the risk sits with whoever carries the forgery.
    /// </summary>
    [DataField]
    public ProtoId<StackPrototype> RebindMaterial = "Cloth";

    [DataField]
    public int RebindCost = 1;

    /// <summary>
    /// Issuer-side authenticity marker. It is intentionally not exposed by the editing UI. A
    /// document issued while this is false is filled in like any other but never gains a
    /// registry record, so a checker machine finds nothing to print for it.
    /// </summary>
    [DataField]
    public bool Authentic = true;

    /// <summary>
    /// The issuing registry's copy of the identity this document was issued with. It is never
    /// networked to clients and never reachable from the editing UI, so a forger can change what
    /// the passport reads but not what the issuer recorded. A checker machine prints this copy
    /// verbatim and leaves the comparison to whoever is reading it.
    /// </summary>
    [DataField]
    public PassportRecord? Record;
}

/// <summary>
/// A frozen copy of the identity fields as the issuer recorded them. Deliberately separate from
/// <see cref="PassportComponent"/> so editing the document cannot touch it.
/// </summary>
[DataDefinition]
public sealed partial class PassportRecord
{
    [DataField]
    public string FullName = string.Empty;

    [DataField]
    public int Age;

    [DataField]
    public string Species = string.Empty;

    [DataField]
    public string Sex = string.Empty;

    [DataField]
    public int HeightCm;

    [DataField]
    public string Nationality = string.Empty;

    [DataField]
    public string Employer = string.Empty;

    [DataField]
    public string Lifepath = string.Empty;

    [DataField]
    public string PassportId = string.Empty;

    [DataField]
    public int IssueYear;

    [DataField]
    public int ExpirationYear;
}

/// <summary>
/// Marks a dispenser as a passport verifier. Structured passports are inspected without being
/// consumed; the dispenser's legacy prototype mappings remain available for old passports.
/// </summary>
[RegisterComponent]
public sealed partial class PassportCheckerComponent : Component;

[Serializable, NetSerializable]
public enum PassportUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class PassportBoundUserInterfaceState(
    string cover,
    string fullName,
    int age,
    string species,
    string sex,
    int heightCm,
    string nationality,
    string employer,
    string lifepath,
    string passportId,
    int issueYear,
    int expirationYear) : BoundUserInterfaceState
{
    public string Cover { get; } = cover;
    public string FullName { get; } = fullName;
    public int Age { get; } = age;
    public string Species { get; } = species;
    public string Sex { get; } = sex;
    public int HeightCm { get; } = heightCm;
    public string Nationality { get; } = nationality;
    public string Employer { get; } = employer;
    public string Lifepath { get; } = lifepath;
    public string PassportId { get; } = passportId;
    public int IssueYear { get; } = issueYear;
    public int ExpirationYear { get; } = expirationYear;
}

[Serializable, NetSerializable]
public sealed class PassportSaveMessage(
    string cover,
    string fullName,
    int age,
    string species,
    string sex,
    int heightCm,
    string nationality,
    string employer,
    string lifepath,
    string passportId,
    int issueYear,
    int expirationYear) : BoundUserInterfaceMessage
{
    public string Cover { get; } = cover;
    public string FullName { get; } = fullName;
    public int Age { get; } = age;
    public string Species { get; } = species;
    public string Sex { get; } = sex;
    public int HeightCm { get; } = heightCm;
    public string Nationality { get; } = nationality;
    public string Employer { get; } = employer;
    public string Lifepath { get; } = lifepath;
    public string PassportId { get; } = passportId;
    public int IssueYear { get; } = issueYear;
    public int ExpirationYear { get; } = expirationYear;
}
