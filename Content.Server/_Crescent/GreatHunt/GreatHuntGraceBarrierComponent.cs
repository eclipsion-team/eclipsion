namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// Marks one side of a Great Hunt preparation wall. <see cref="GreatHuntRuleSystem"/> builds these around each home
/// base when the hunt starts and deletes every marked grid the moment the preparation phase ends, whether it ran
/// out on its own or an admin skipped it with <c>greathunt_skipgrace</c>.
/// </summary>
[RegisterComponent]
public sealed partial class GreatHuntGraceBarrierComponent : Component
{
}
