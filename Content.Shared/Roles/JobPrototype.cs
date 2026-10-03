using Content.Shared.Access;
using Content.Shared.Guidebook;
using Content.Shared.Customization.Systems;
using Content.Shared.Dataset;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Content.Shared.StatusIcon;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Content.Shared._Crescent.Ranks;
using Content.Shared._Crescent.Religion;

namespace Content.Shared.Roles
{
    /// <summary>
    ///     Describes information for a single job on the station.
    /// </summary>
    [Prototype("job")]
    public sealed partial class JobPrototype : IPrototype
    {
        [ViewVariables]
        [IdDataField]
        public string ID { get; private set; } = default!;

        [DataField("playTimeTracker", required: true)]
        public ProtoId<PlayTimeTrackerPrototype> PlayTimeTracker { get; private set; } = string.Empty;

        [DataField("supervisors")]
        public string Supervisors { get; private set; } = "nobody";

        /// <summary>
        ///     The name of this job as displayed to players.
        /// </summary>
        [DataField("name")]
        public string Name { get; private set; } = string.Empty;

        [ViewVariables(VVAccess.ReadOnly)]
        public string LocalizedName => Loc.GetString(Name);

        /// <summary>
        ///     The name of this job as displayed to players.
        /// </summary>
        [DataField("description")]
        public string? Description { get; private set; }

        [ViewVariables(VVAccess.ReadOnly)]
        public string? LocalizedDescription => Description is null ? null : Loc.GetString(Description);

        [DataField("requirements")]
        public List<CharacterRequirement>? Requirements;

        [DataField("joinNotifyCrew")]
        public bool JoinNotifyCrew { get; private set; } = false;

        [DataField("requireAdminNotify")]
        public bool RequireAdminNotify { get; private set; } = false;

        [DataField("setPreference")]
        public bool SetPreference { get; private set; } = true;

        /// <summary>
        ///     Whether this job should show in the ID Card Console.
        ///     If set to null, it will default to SetPreference's value.
        /// </summary>
        [DataField]
        public bool? OverrideConsoleVisibility { get; private set; } = null;

        [DataField("canBeAntag")]
        public bool CanBeAntag { get; private set; } = true;

        /// <summary>
        /// Used by Contractors to determine if a given job should have a passport
        /// This should be disabled for Borgs and Station AI, for example.
        /// </summary>
        [DataField("canHavePassport")]
        public bool CanHavePassport { get; private set; } = true;

        /// <summary>
        ///     Crescent - the faith this job has to hold. Command roles lead their faction's faith, so whoever takes
        ///     one spawns into it whatever their profile says, and cannot renounce it at an altar.
        /// </summary>
        [DataField]
        public ProtoId<ReligionPrototype>? RequiredReligion { get; private set; }

        /// <summary>
        /// Nyano/DV: For e.g. prisoners, they'll never use their latejoin spawner.
        /// </summary>
        [DataField("alwaysUseSpawner")]
        public bool AlwaysUseSpawner { get; private set; } = false;

        /// <summary>
        ///     Whether this job is a head.
        ///     The job system will try to pick heads before other jobs on the same priority level.
        /// </summary>
        [DataField("weight")]
        public int Weight { get; private set; }

        /// <summary>
        /// How to sort this job relative to other jobs in the UI.
        /// Jobs with a higher value with sort before jobs with a lower value.
        /// If not set, <see cref="Weight"/> is used as a fallback.
        /// </summary>
        [DataField]
        public int? DisplayWeight { get; private set; }

        public int RealDisplayWeight => DisplayWeight ?? Weight;

        /// <summary>
        ///     A numerical score for how much easier this job is for antagonists.
        ///     For traitors, reduces starting TC by this amount. Other gamemodes can use it for whatever they find fitting.
        /// </summary>
        [DataField("antagAdvantage")]
        public int AntagAdvantage = 0;

        [DataField("startingGear")]
        public ProtoId<StartingGearPrototype>? StartingGear { get; private set; }

        /// <summary>
        ///     If this has a value, it will randomly set the entity name of the
        ///     entity upon spawn based on the dataset.
        /// </summary>
        [DataField]
        public ProtoId<LocalizedDatasetPrototype>? NameDataset;

        /// <summary>
        ///   A list of requirements that when satisfied, add or replace from the base starting gear.
        /// </summary>
        [DataField("conditionalStartingGear")]
        public List<ConditionalStartingGear>? ConditionalStartingGears { get; private set; }

        /// <summary>
        /// Use this to spawn in as a non-humanoid (borg, test subject, etc.)
        /// Starting gear will be ignored.
        /// If you want to just add special attributes to a humanoid, use AddComponentSpecial instead.
        /// </summary>
        [DataField("jobEntity")]
        public EntProtoId? JobEntity = null;

        /// <summary>
        /// Optional canonical humanoid identity applied only while spawning as this job.
        /// The player's saved character profile is not modified.
        /// </summary>
        [DataField("characterOverride")]
        public JobCharacterOverride? CharacterOverride { get; private set; }

        [DataField]
        public ProtoId<JobIconPrototype> Icon { get; private set; } = "JobIconUnknown";

        [DataField("special", serverOnly: true)]
        public JobSpecial[] Special { get; private set; } = Array.Empty<JobSpecial>();

        [DataField("afterLoadoutSpecial", serverOnly: true)]
        public JobSpecial[] AfterLoadoutSpecial { get; private set; } = [];

        [DataField("access")]
        public IReadOnlyCollection<ProtoId<AccessLevelPrototype>> Access { get; private set; } = Array.Empty<ProtoId<AccessLevelPrototype>>();

        [DataField("accessGroups")]
        public IReadOnlyCollection<ProtoId<AccessGroupPrototype>> AccessGroups { get; private set; } = Array.Empty<ProtoId<AccessGroupPrototype>>();

        [DataField("extendedAccess")]
        public IReadOnlyCollection<ProtoId<AccessLevelPrototype>> ExtendedAccess { get; private set; } = Array.Empty<ProtoId<AccessLevelPrototype>>();

        [DataField("extendedAccessGroups")]
        public IReadOnlyCollection<ProtoId<AccessGroupPrototype>> ExtendedAccessGroups { get; private set; } = Array.Empty<ProtoId<AccessGroupPrototype>>();

        [DataField]
        public bool Whitelisted;

        /// <summary>
        /// Optional shared whitelist key. Every job declaring the same group is covered by a single whitelist
        /// entry, so admins whitelist the group once instead of granting each job separately.
        /// </summary>
        [DataField]
        public string? WhitelistGroup;

        /// <summary>
        /// The key this job's whitelist entry is stored under: its group if it has one, otherwise its own ID.
        /// </summary>
        public string WhitelistKey => WhitelistGroup ?? ID;

        [DataField]
        public bool SpawnLoadout = true;

        [DataField]
        public bool ApplyTraits = true;

        /// <summary>
        /// Optional list of guides associated with this role. If the guides are opened, the first entry in this list
        /// will be used to select the currently selected guidebook.
        /// </summary>
        [DataField]
        public List<ProtoId<GuideEntryPrototype>>? Guides;

        [DataField]
        public Dictionary<ProtoId<RankPrototype>, HashSet<CharacterRequirement>?>? Ranks;

        /// <summary>
        /// Optional chat styling amplification for command jobs.
        /// </summary>
        [DataField("chatAmplification")]
        public JobChatAmplification? ChatAmplification { get; private set; }

        /// <summary>
        /// Optional override for speaker name color in chat output.
        /// </summary>
        [DataField("chatNameColor")]
        public Color? ChatNameColor { get; private set; }

		// Rat-start
        [DataField]
        public bool SingleLifeRound = false;
		// Rat-start
    }

    [DataDefinition]
    public sealed partial class JobChatAmplification
    {
        /// <summary>
        /// Multiplier for regular local speech in chat and speech bubbles.
        /// </summary>
        [DataField("bubbleScale")]
        public float BubbleScale = 1f;

        /// <summary>
        /// Multiplier for local speech ending with !! in chat and speech bubbles.
        /// </summary>
        [DataField("shoutBubbleScale")]
        public float ShoutBubbleScale = 1f;

        /// <summary>
        /// Multiplier for radio message size.
        /// </summary>
        [DataField("radioScale")]
        public float RadioScale = 1f;

        /// <summary>
        /// Forces radio messages to render bold.
        /// </summary>
        [DataField("radioBold")]
        public bool RadioBold = true;
    }

    /// <summary>
    ///   Starting gear that will only be applied upon satisfying requirements.
    /// </summary>
    [DataDefinition]
    public sealed partial class ConditionalStartingGear
    {
        /// <summary>
        ///   The requirements to check.
        /// </summary>
        [DataField(required: true)]
        public List<CharacterRequirement> Requirements;

        /// <summary>
        ///   The starting gear to apply, replacing the equivalent slots.
        /// </summary>
        [DataField(required: true)]
        public ProtoId<StartingGearPrototype> Id { get; private set; }

    }

    /// <summary>
    /// Sorts <see cref="JobPrototype"/>s appropriately for display in the UI,
    /// respecting their <see cref="JobPrototype.Weight"/>.
    /// </summary>
    public sealed class JobUIComparer : IComparer<JobPrototype>
    {
        public static readonly JobUIComparer Instance = new();

        public int Compare(JobPrototype? x, JobPrototype? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (ReferenceEquals(null, y))
                return 1;
            if (ReferenceEquals(null, x))
                return -1;

            var cmp = -x.RealDisplayWeight.CompareTo(y.RealDisplayWeight);
            if (cmp != 0)
                return cmp;
            return string.Compare(x.ID, y.ID, StringComparison.Ordinal);
        }
    }
}
