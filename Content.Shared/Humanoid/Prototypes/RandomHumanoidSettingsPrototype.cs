using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;

namespace Content.Shared.Humanoid.Prototypes;

/// <summary>
///     This is what is used to change a humanoid spawned by RandomHumanoidSystem in Content.Server.
/// </summary>
[Prototype("randomHumanoidSettings")]
public sealed partial class RandomHumanoidSettingsPrototype : IPrototype, IInheritingPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;

    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<RandomHumanoidSettingsPrototype>))]
    public string[]? Parents { get; private set; }

    [AbstractDataField]
    public bool Abstract { get; private set; }

    /// <summary>
    ///     Whether the humanoid's name should take from the randomized profile or not.
    /// </summary>
    [DataField("randomizeName")]
    public bool RandomizeName { get; private set; } = true;

    /// <summary>
    ///     Species that will be ignored by the randomizer.
    /// </summary>
    [DataField("speciesBlacklist")]
    public HashSet<string> SpeciesBlacklist { get; private set; } = new();

    /// <summary>
    ///     Eclipsion - if set, only these species can be rolled and <see cref="SpeciesBlacklist"/> is ignored.
    ///     Use this for lore-locked roles so a species made round-start later can't slip past a blacklist.
    /// </summary>
    [DataField("speciesWhitelist")]
    public HashSet<ProtoId<SpeciesPrototype>> SpeciesWhitelist { get; private set; } = new();

    /// <summary>
    ///     Extra components to add to this entity.
    /// </summary>
    [DataField("components")]
    public ComponentRegistry? Components { get; private set; }
}
