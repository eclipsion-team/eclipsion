using Content.Shared.Construction.Prototypes;
using Content.Shared.Research.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Lathe
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
    public sealed partial class LatheComponent : Component
    {
        /// <summary>
        /// All of the recipes that the lathe has by default
        /// </summary>
        [DataField]
        public List<ProtoId<LatheRecipePrototype>> StaticRecipes = new();

        /// <summary>
        /// All of the recipes that the lathe is capable of researching
        /// </summary>
        [DataField]
        public List<ProtoId<LatheRecipePrototype>> DynamicRecipes = new();

        /// <summary>
        /// Recipes to drop from whatever this lathe inherited, for prototypes that only want part of a parent's list
        /// </summary>
        [DataField]
        public List<ProtoId<LatheRecipePrototype>> ExcludedRecipes = new();

        /// <summary>
        /// The lathe's construction queue
        /// </summary>
        [DataField]
        public List<LatheRecipePrototype> Queue = new();

        /// <summary>
        /// The sound that plays when the lathe is producing an item, if any
        /// </summary>
        [DataField]
        public SoundSpecifier? ProducingSound;

        [DataField]
        public string? ReagentOutputSlotId;

        /// <summary>
        /// Empty container spawned for reagent output that doesn't fit in the slot. Null spills it instead.
        /// </summary>
        [DataField]
        public EntProtoId? ReagentOutputFallback;

        /// <summary>
        /// The default amount that's displayed in the UI for selecting the print amount.
        /// </summary>
        [DataField, AutoNetworkedField]
        public int DefaultProductionAmount = 1;

        #region Visualizer info
        [DataField]
        public string? IdleState;

        [DataField]
        public string? RunningState;
        #endregion

        /// <summary>
        /// The recipe the lathe is currently producing
        /// </summary>
        [ViewVariables]
        public LatheRecipePrototype? CurrentRecipe;

        #region Batch output
        /// <summary>
        /// Hold every result back until the whole queue is done and then drop it in one go, instead of
        /// spitting out one entity per finished recipe. Meant for machines people feed a whole shift's worth
        /// of work at once, like the ore processor: a 500 item run otherwise means 500 spawns, 500 stack
        /// merges and 500 sounds spread over the run.
        /// </summary>
        [DataField]
        public bool BatchOutput;

        /// <summary>
        /// Results finished but not handed over yet, as prototype -> how many times it was made.
        /// Only used when <see cref="BatchOutput"/> is set. Flushed when the queue runs dry.
        /// </summary>
        [DataField]
        public Dictionary<EntProtoId, int> PendingOutput = new();

        /// <summary>
        /// How many copies a single queue request is allowed to add. Materials are the real limit; this just
        /// stops one click from spending an entire silo. Machines meant to be fed in bulk raise it.
        ///
        /// Don't raise it on a lathe that can make a recipe with a completetime of 0: those finish the moment
        /// they start, so the lathe chases the whole queue down in a single tick instead of one item per tick.
        /// </summary>
        [DataField]
        public int MaxQueuePerRequest = 100;
        #endregion

        #region Recipe cache
        /// <summary>
        /// Cached result of the currently-available recipe list. Rebuilding this walks every static and
        /// dynamic recipe, which is thousands of entries on the larger lathes, so it's only recomputed when
        /// something that can actually change the set happens (research, emag, prototype reload).
        /// </summary>
        [ViewVariables]
        public List<ProtoId<LatheRecipePrototype>>? CachedRecipes;

        /// <summary>
        /// Set version of <see cref="CachedRecipes"/>, for O(1) membership tests.
        /// </summary>
        [ViewVariables]
        public HashSet<ProtoId<LatheRecipePrototype>>? CachedRecipeLookup;

        /// <summary>
        /// The next time this lathe is allowed to push a new UI state.
        /// </summary>
        [ViewVariables]
        public TimeSpan NextUiUpdate;
        #endregion

        #region MachineUpgrading
        /// <summary>
        /// A modifier that changes how long it takes to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float TimeMultiplier = 1;

        /// <summary>
        /// A modifier that changes how much of a material is needed to print a recipe
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
        public float MaterialUseMultiplier = 1;

        /// <summary>
        /// The prototype's own multipliers, captured before machine parts are applied so that
        /// upgrading doesn't wipe out a lathe variant's built-in bonus (e.g. the industrial ore processor).
        ///
        /// Saved with the entity, and deliberately so. <see cref="TimeMultiplier"/> is itself a DataField, so a
        /// map saved after init stores the part-adjusted value; if the baseline were not saved alongside it,
        /// reloading would capture that adjusted value as the new baseline and apply the part scaling on top of
        /// it again, compounding a little further with every save/load cycle.
        /// </summary>
        [DataField, ViewVariables]
        public float? BaseTimeMultiplier;

        /// <inheritdoc cref="BaseTimeMultiplier"/>
        [DataField, ViewVariables]
        public float? BaseMaterialUseMultiplier;

        /// <summary>
        /// The machine part that reduces print time.
        /// </summary>
        [DataField]
        public ProtoId<MachinePartPrototype> MachinePartPrintSpeed = "Manipulator";

        /// <summary>
        /// The machine part that reduces material cost.
        /// </summary>
        [DataField]
        public ProtoId<MachinePartPrototype> MachinePartMaterialUse = "MatterBin";

        /// <summary>
        /// Print time is multiplied by this raised to (part rating - 1).
        /// </summary>
        [DataField]
        public float PartRatingPrintTimeMultiplier = 0.5f;

        /// <summary>
        /// Material use is multiplied by this raised to (part rating - 1).
        /// </summary>
        [DataField]
        public float PartRatingMaterialUseMultiplier = DefaultPartRatingMaterialUseMultiplier;

        public const float DefaultPartRatingMaterialUseMultiplier = 0.85f;
        #endregion
    }

    public sealed class LatheGetRecipesEvent : EntityEventArgs
    {
        public readonly EntityUid Lathe;

        public bool getUnavailable;

        public List<ProtoId<LatheRecipePrototype>> Recipes = new();

        public LatheGetRecipesEvent(EntityUid lathe, bool forced)
        {
            Lathe = lathe;
            getUnavailable = forced;
        }
    }

    /// <summary>
    /// Event raised on a lathe when it starts producing a recipe.
    /// </summary>
    [ByRefEvent]
    public readonly record struct LatheStartPrintingEvent(LatheRecipePrototype Recipe);

    /// <summary>
    /// Event fired when a lathe finishes producing an item.
    /// </summary>
    public sealed class LatheProduceCompleteEvent : EntityEventArgs
    {
        public EntityUid Lathe;
        public LatheRecipePrototype Recipe;

        public LatheProduceCompleteEvent(EntityUid lathe, LatheRecipePrototype recipe)
        {
            Lathe = lathe;
            Recipe = recipe;
        }
    }
}
