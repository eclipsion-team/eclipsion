using System.Linq;
using System.Text.Json;
using Content.Shared._Crescent.Diplomacy;
using Content.Shared.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Diplomacy;

/// <summary>
/// Cross-round persistence for the faction relations players set from the diplomacy console.
///
/// The relation table lived only in the system's own field, which survives a round restart (entity systems
/// are not rebuilt between rounds) but dies with the server process. That made the war state look persistent
/// right up until the next server restart wiped it. Wars are supposed to carry: a faction that ends a round
/// at war starts the next one at war, until someone signs a peace or an admin resets it.
///
/// Pending proposals deliberately do NOT carry. An offer is made to the people on shift; holding it open
/// across a round restart means next round's crew inherits a deal they never heard about.
/// </summary>
public sealed partial class RatDiplomacySystem
{
    [Dependency] private readonly IResourceManager _res = default!;

    private static readonly ResPath SavePath = new("/faction_diplomacy.json");
    private const int SaveVersion = 1;

    /// <summary>Set while replaying the save file, so applying it does not write it straight back out.</summary>
    private bool _loading;

    private void InitializePersistence()
    {
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        foreach (var pending in _pending.Values)
            pending.Clear();
    }

    /// <summary>Pair keys are ordered so a relation is stored once, not once per direction.</summary>
    private static string PairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private void Load()
    {
        try
        {
            if (!_res.UserData.TryReadAllText(SavePath, out var json))
                return;

            var saved = JsonSerializer.Deserialize<FactionDiplomacySaveData>(json);
            if (saved is null || saved.Version != SaveVersion)
                return;

            _loading = true;

            foreach (var (key, relation) in saved.Relations)
            {
                var pair = key.Split('|');
                if (pair.Length != 2)
                    continue;

                // A faction that has since been removed from the roster, or a relation that no longer
                // exists, is dropped rather than resurrected.
                if (!AllFactions.Contains(pair[0]) || !AllFactions.Contains(pair[1]))
                    continue;

                if (!Enum.TryParse<FactionRelation>(relation, out var parsed))
                    continue;

                // Goes through SetRelation so permanent-enemy pairs stay locked even if the save says otherwise.
                SetRelation(pair[0], pair[1], parsed);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load faction diplomacy: {e}");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Writes the whole relation table out. Called on every relation change — that only happens when someone
    /// works a diplomacy console, so this is a handful of writes a round, not a hot path.
    /// </summary>
    private void Save()
    {
        if (_loading)
            return;

        try
        {
            var data = new FactionDiplomacySaveData { Version = SaveVersion };

            foreach (var (faction, relations) in _relations)
            {
                foreach (var (other, relation) in relations)
                {
                    // Only departures from the starting position are stored. Skipping Neutral instead lost a pair
                    // that starts at war and was talked down to neutral (peace, then trade, then the trade broken):
                    // the next server restart put it straight back at war.
                    if (relation == DefaultRelation(faction, other))
                        continue;

                    data.Relations[PairKey(faction, other)] = relation.ToString();
                }
            }

            _res.UserData.WriteAllText(SavePath, JsonSerializer.Serialize(data));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction diplomacy: {e}");
        }
    }

    /// <summary>
    /// Throws away everything that carried over and puts the sector back to its written-in starting position.
    /// Admin use only — a war stuck in a state nobody can talk their way out of has no other exit.
    /// </summary>
    public void ResetRelations()
    {
        InitRelations();
        Save();
        RefreshAll();
    }

    /// <summary>The relations currently in force, for admin readout.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, FactionRelation>> Relations => _relations;
}

internal sealed class FactionDiplomacySaveData
{
    public int Version { get; set; }

    /// <summary>"FactionA|FactionB" (ordered) to relation name. Pairs still at their starting relation are absent.</summary>
    public Dictionary<string, string> Relations { get; set; } = new();
}
