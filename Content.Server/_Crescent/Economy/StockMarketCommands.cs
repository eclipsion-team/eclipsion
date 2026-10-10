using System.Linq;
using Content.Server.Administration;
using Content.Server.Cargo.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Crescent.Economy;

/// <summary>
/// Admin control over the sector stock market. Prices persist across rounds, so a market that ends up
/// in a bad state stays there until someone intervenes — this is that intervention.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class StockMarketCommand : IConsoleCommand
{
    public string Command => "stockmarket";
    public string Description => Loc.GetString("cmd-stockmarket-desc");
    public string Help => Loc.GetString("cmd-stockmarket-help", ("command", Command));

    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteLine(Help);
            return;
        }

        var stocks = _systems.GetEntitySystem<StockCompanySystem>();

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                foreach (var company in stocks.GetCompanies())
                {
                    shell.WriteLine(
                        $"{company.Id,-24} {company.CurrentPrice,10:0.00} cr  " +
                        $"x{company.PriceMultiplier:0.000}  {company.Share * 100f:0.0}% share  " +
                        $"{(company.Active ? "listed" : "DELISTED")}");
                }
                shell.WriteLine(stocks.Frozen ? "market is FROZEN" : "market is running");
                return;

            case "reset":
                stocks.ResetMarket();
                shell.WriteLine(Loc.GetString("cmd-stockmarket-reset"));
                return;

            case "freeze":
                stocks.Frozen = !stocks.Frozen;
                shell.WriteLine(stocks.Frozen
                    ? Loc.GetString("cmd-stockmarket-frozen")
                    : Loc.GetString("cmd-stockmarket-unfrozen"));
                return;

            case "clearportfolios":
                _systems.GetEntitySystem<StockPortfolioPersistenceSystem>().ClearAll();
                shell.WriteLine(Loc.GetString("cmd-stockmarket-portfolios-cleared"));
                return;

            case "shock":
                ExecuteShock(shell, args, stocks);
                return;

            default:
                shell.WriteLine(Help);
                return;
        }
    }

    private static void ExecuteShock(IConsoleShell shell, string[] args, StockCompanySystem stocks)
    {
        if (args.Length < 3)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        // float.TryParse accepts "NaN" and "Infinity"; either would poison a price that persists across rounds.
        if (!float.TryParse(args[2], out var percent) || !float.IsFinite(percent))
        {
            shell.WriteError(Loc.GetString("shell-argument-must-be-number"));
            return;
        }

        var ticks = 4;
        if (args.Length > 3 && !int.TryParse(args[3], out ticks))
        {
            shell.WriteError(Loc.GetString("shell-argument-must-be-number"));
            return;
        }

        if (ticks <= 0)
        {
            shell.WriteError(Loc.GetString("shell-argument-must-be-number"));
            return;
        }

        if (!stocks.AddPressure(args[1], percent / 100f, ticks, Loc.GetString("stock-news-admin")))
        {
            shell.WriteError(Loc.GetString("cmd-stockmarket-unknown-company", ("id", args[1])));
            return;
        }

        shell.WriteLine(Loc.GetString("shell-command-success"));
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(
                new[] { "list", "reset", "freeze", "shock", "clearportfolios" },
                Loc.GetString("cmd-stockmarket-hint-subcommand"));
        }

        if (args.Length == 2 && args[0].Equals("shock", StringComparison.OrdinalIgnoreCase))
        {
            var stocks = _systems.GetEntitySystem<StockCompanySystem>();
            return CompletionResult.FromHintOptions(
                stocks.GetCompanies().Select(c => c.Id),
                Loc.GetString("cmd-stockmarket-hint-company"));
        }

        return CompletionResult.Empty;
    }
}
