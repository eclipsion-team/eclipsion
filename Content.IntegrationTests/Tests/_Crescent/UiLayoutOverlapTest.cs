using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._Crescent.FactionPortal;
using Content.Client._Crescent.LagMonitor;
using Content.Client._Crescent.NpcSquad;
using Content.Client._Crescent.Territory;
using Content.Shared._Crescent.FactionPortal;
using Content.Shared._Crescent.LagMonitor;
using Content.Shared._Crescent.NpcSquad;
using Content.Shared._Crescent.Territory;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Crescent;

// opens our windows with long sample text and looks for text that spills over other controls
[TestFixture]
public sealed class UiLayoutOverlapTest
{
    private const string LongName = "Very Long Name Of Something Important Mk II";

    [Test]
    public async Task LagMonitorWindow()
    {
        await Run(() =>
        {
            var sample = new LagSample
            {
                Time = DateTime.Now,
                Load = 1f,
                TickRate = 0.2f,
                TargetTickRate = 30,
                TickMs = 5314.6f,
                GcPauseMs = 540,
                Gen2 = 1,
                Players = 64,
                Entities = 92267,
                Grids = 136,
                Npcs = 192,
                ActiveNpcs = 186,
                AwakeBodies = 2837,
                Drones = 19,
                MonitorMs = 0.278f,
            };
            for (var i = 0; i < 15; i++)
            {
                sample.Systems.Add(new LagEntry("WorldControllerSystem" + i, 3935.52f));
                sample.Parts.Add(new LagEntry("PVS Serialize States" + i, 199.24f));
            }

            var state = new LagMonitorEuiState
            {
                Enabled = true,
                Current = sample,
                Peak = sample,
                LoadHistory = Enumerable.Range(0, 120).Select(i => i / 100f).ToArray(),
                Findings =
                {
                    Loc.GetString("lag-monitor-finding-cpu", ("load", 100), ("tps", "0.2"), ("target", 30),
                        ("cause", "WorldControllerSystem 3935 ms, NodeGroupSystem 424 ms, PhysicsSystem 209 ms")),
                    Loc.GetString("lag-monitor-finding-ok"),
                },
            };
            for (var i = 0; i < 5; i++)
            {
                state.Hitches.Add(new LagHitch
                {
                    Time = DateTime.Now,
                    Ms = 5300,
                    Top = { new("WorldControllerSystem", 3935), new("NodeGroupSystem", 424), new("PhysicsSystem", 209) },
                });
                state.Pings.Add(new LagPing(LongName, 300));
            }

            var window = new LagMonitorWindow();
            window.UpdateState(state);
            return window;
        });
    }

    [Test]
    public async Task DroneBayWindow()
    {
        await Run(() =>
        {
            var state = new TerritoryDroneBayState
            {
                Owner = "IND1",
                Level = 3,
                Powered = true,
                CanCommand = true,
                Bought = 2,
                Limit = 4,
                AttackRange = 1500,
                Payment = Payment(),
            };
            for (var i = 0; i < 3; i++)
            {
                state.Hulls.Add(new TerritoryDroneBayHull { Id = "hull" + i, Name = LongName, Price = 1234567 });
                state.Targets.Add(new TerritoryDroneTarget { Target = new NetEntity(100 + i), Name = LongName, Faction = "IND2", Distance = 1499 });
                state.Drones.Add(new TerritoryDroneEntry
                {
                    Drone = new NetEntity(200 + i),
                    Name = LongName,
                    Bought = i == 0,
                    Order = (TerritoryDroneOrder) i,
                    Target = LongName,
                    Engaging = true,
                    Distance = 1499,
                    Integrity = 50,
                });
            }

            var window = new TerritoryDroneBayWindow();
            window.UpdateState(state);
            return window;
        });
    }

    [Test]
    public async Task RecruitmentWindow()
    {
        await Run(() =>
        {
            var window = new TerritoryRecruitmentWindow();
            window.UpdateState(new TerritoryRecruitmentState
            {
                Owner = "IND1",
                Level = 3,
                Powered = true,
                Roles = { "Rifleman", "Breacher", "Shocktrooper", "Marksman", "Machinegunner" },
                EscortSurcharge = 500,
                Garrison = 1,
                GarrisonLimit = 3,
                Escorts = 1,
                EscortLimit = 2,
                SquadRoom = 1,
                Payment = Payment(),
            });
            return window;
        });
    }

    [Test]
    public async Task AdministrationWindow()
    {
        await Run(() =>
        {
            var state = new PersistentPoiAdministrationState
            {
                Owner = "IND1",
                RegionName = LongName,
                Level = 2,
                MaxLevel = 3,
                CustomName = LongName,
                CanUpgrade = true,
                HasSilo = true,
                HasDrones = true,
                Independent = true,
                FounderName = LongName,
                MaxMembers = 4,
            };
            foreach (var category in Enum.GetValues<PersistentPoiWorksCategory>())
            {
                state.Limits.Add(new PersistentPoiLimitState { Category = category, Used = 12, Limit = 20, NextLimit = 30 });
            }

            for (var i = 0; i < 4; i++)
            {
                state.NextLevelCost.Add(new PersistentPoiCostState { MaterialId = "Steel", Material = LongName, Deposited = 12345, Required = 50000, InSilo = 99999 });
                state.Members.Add(new PersistentPoiMemberState { Name = LongName, CoOwner = i == 0 });
                state.Candidates.Add(new PersistentPoiCandidateState { Body = new NetEntity(300 + i), Name = LongName });
            }

            var window = new PersistentPoiAdministrationWindow();
            window.Panel.UpdateState(state);
            return window;
        });
    }

    [Test]
    public async Task FactionPortalWindow()
    {
        await Run(() =>
        {
            var window = new FactionPortalWindow();
            window.UpdateState(new FactionPortalBuiState(true, Enumerable.Range(0, 6)
                .Select(i => new FactionPortalDestination(new NetEntity(400 + i), LongName))
                .ToList()));
            return window;
        });
    }

    [Test]
    public async Task NpcSquadWindow()
    {
        await Run(() =>
        {
            var members = Enumerable.Range(0, 4).Select(i => new NpcSquadMemberState
            {
                Entity = new NetEntity(500 + i),
                Name = LongName,
                Order = (NpcSquadOrder) (i % 2),
                Condition = (NpcSquadMemberCondition) i,
                Health = 0.4f,
                SpareMagazines = 3,
            }).ToList();

            var window = new NpcSquadWindow();
            window.UpdateState(new NpcSquadBuiState(members, 4, default, false));
            return window;
        });
    }

    private static TerritoryPaymentState Payment()
    {
        return new TerritoryPaymentState
        {
            HasPersonal = true,
            PersonalBalance = 123456789,
            Faction = "IND1",
            HasTreasury = true,
            TreasuryBalance = 123456789,
            TreasuryAllowance = 1234567,
            TreasuryShare = 0.25f,
        };
    }

    private static async Task Run(Func<BaseWindow> make)
    {
        await using var pair = await PoolManager.GetServerClient();
        var client = pair.Client;

        BaseWindow window = default!;
        var problems = new List<string>();

        await client.WaitPost(() =>
        {
            window = make();
            window.OpenCentered();
        });
        await pair.RunTicksSync(5);

        // default size and the smallest the player can drag it to, every tab
        foreach (var small in new[] { false, true })
        {
            if (small)
            {
                await client.WaitPost(() => window.SetSize = window.MinSize);
                await pair.RunTicksSync(5);
            }

            var tabs = 1;
            await client.WaitPost(() => tabs = Math.Max(1, Tabs(window).Select(t => t.ChildCount).DefaultIfEmpty(1).Max()));

            for (var tab = 0; tab < tabs; tab++)
            {
                var index = tab;
                await client.WaitPost(() =>
                {
                    foreach (var container in Tabs(window))
                    {
                        if (index < container.ChildCount)
                            container.CurrentTab = index;
                    }
                });
                await pair.RunTicksSync(5);

                await client.WaitPost(() =>
                {
                    var where = $"{window.GetType().Name} ({(small ? "min size" : "default size")}, tab {index})";
                    Check(window, where, problems);
                });
            }
        }

        await client.WaitPost(() => window.Close());

        Assert.That(problems.Distinct().ToList(), Is.Empty,
            string.Join("\n", problems.Distinct().Take(60)));

        await pair.CleanReturnAsync();
    }

    private static IEnumerable<TabContainer> Tabs(Control control)
    {
        if (control is TabContainer tabs)
            yield return tabs;

        foreach (var child in control.Children)
        {
            foreach (var found in Tabs(child))
            {
                yield return found;
            }
        }
    }

    private static string Describe(Control control)
    {
        var text = control switch
        {
            Label label => $" \"{label.Text}\"",
            Button button => $" \"{button.Text}\"",
            _ => string.Empty,
        };
        return $"{control.Name ?? control.GetType().Name}{text}";
    }

    private static void Check(Control control, string where, List<string> problems)
    {
        if (!control.Visible)
            return;

        switch (control)
        {
            case Label { ClipText: false } label when !string.IsNullOrEmpty(label.Text):
            {
                label.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));
                var want = label.DesiredSize - new Vector2(label.Margin.SumHorizontal, label.Margin.SumVertical);
                if (want.X > label.Width + 2f)
                    problems.Add($"{where}: text wider than its box ({want.X:0} > {label.Width:0}) in {Describe(label.Parent!)}: {Describe(label)}");
                break;
            }
            case RichTextLabel rich:
            {
                rich.Measure(new Vector2(rich.Width + rich.Margin.SumHorizontal, float.PositiveInfinity));
                var want = rich.DesiredSize.Y - rich.Margin.SumVertical;
                if (want > rich.Height + 2f)
                    problems.Add($"{where}: wrapped text taller than its box ({want:0} > {rich.Height:0}) in {Describe(rich.Parent!)}");
                break;
            }
        }

        // a scroll container's content is meant to be bigger than it
        if (control is not ScrollContainer)
        {
            foreach (var child in control.Children)
            {
                if (!child.Visible)
                    continue;

                var rect = UIBox2.FromDimensions(child.Position, child.Size);
                if (rect.Left < -1f || rect.Top < -1f || rect.Right > control.Width + 1f || rect.Bottom > control.Height + 1f)
                {
                    problems.Add($"{where}: {Describe(child)} spills out of {Describe(control)} " +
                                 $"(child {rect.Right:0}x{rect.Bottom:0}, parent {control.Width:0}x{control.Height:0})");
                }
            }
        }

        if (control is BoxContainer or GridContainer)
        {
            var kids = control.Children.Where(c => c.Visible).ToList();
            for (var i = 0; i < kids.Count; i++)
            {
                var a = UIBox2.FromDimensions(kids[i].Position, kids[i].Size);
                for (var j = i + 1; j < kids.Count; j++)
                {
                    var b = UIBox2.FromDimensions(kids[j].Position, kids[j].Size);
                    var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                    if (overlapX > 1f && overlapY > 1f)
                        problems.Add($"{where}: {Describe(kids[i])} and {Describe(kids[j])} overlap in {Describe(control)}");
                }
            }
        }

        foreach (var child in control.Children)
        {
            Check(child, where, problems);
        }
    }
}
