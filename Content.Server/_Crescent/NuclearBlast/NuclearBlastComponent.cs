namespace Content.Server._Crescent.NuclearBlast;

/// <summary>
/// Turns this entity's detonation into a nuclear blast. Everything within <see cref="Radius"/> tiles is wiped out:
/// every body is gibbed outright regardless of armour, and every item, mob and destructible entity is erased.
/// The entity's <see cref="Content.Server.Explosion.Components.ExplosiveComponent"/>, if it has one, still goes off
/// on top, sized to the same radius, for the fireball, sound and hull damage.
/// </summary>
/// <remarks>
/// Ordinary explosions cap the damage they deal to a body and never touch items without a destructible, so even a
/// huge one leaves people standing and the floor covered in loot. This exists for the blasts that should not.
/// </remarks>
[RegisterComponent, Access(typeof(NuclearBlastSystem))]
public sealed partial class NuclearBlastComponent : Component
{
    /// <summary>
    /// Radius of the blast, in tiles.
    /// </summary>
    [DataField]
    public float Radius = 28f;

    /// <summary>
    /// Set once the blast has gone off, so it cannot go off twice.
    /// </summary>
    [ViewVariables]
    public bool Detonated;
}
