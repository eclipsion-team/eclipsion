using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Crescent.GreatHunt;

/// <summary>
/// The Great Hunt's counterpart to <c>unionfall_skipgrace</c>: ends the preparation phase on the spot, bringing the
/// walls around both home bases down and waking the Great Altar.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class GreatHuntSkipGraceCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entityManager = default!;

    public string Command => "greathunt_skipgrace";
    public string Description => "Ends the Great Hunt preparation phase immediately: the base walls fall and the altar wakes.";
    public string Help => "Usage: greathunt_skipgrace";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var skipped = _entityManager.System<GreatHuntRuleSystem>().SkipGracePeriod();

        shell.WriteLine(skipped > 0
            ? "Great Hunt preparation phase skipped. The walls are down and the altar is awake."
            : "No Great Hunt is in its preparation phase.");
    }
}
