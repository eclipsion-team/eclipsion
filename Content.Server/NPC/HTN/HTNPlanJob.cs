using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.CPUJob.JobQueues;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.HTN;

/// <summary>
/// A time-sliced job that will retrieve an HTN plan eventually.
/// </summary>
public sealed class HTNPlanJob : Job<HTNPlan>
{
    private readonly HTNTask _rootTask;
    private NPCBlackboard _blackboard;

    private IPrototypeManager _protoManager;

    /// <summary>
    /// Branch traversal of an existing plan (if applicable).
    /// </summary>
    private List<int>? _branchTraversal;

    public HTNPlanJob(
        double maxTime,
        IPrototypeManager protoManager,
        HTNTask rootTask,
        NPCBlackboard blackboard,
        List<int>? branchTraversal,
        CancellationToken cancellationToken = default) : base(maxTime, cancellationToken)
    {
        _protoManager = protoManager;
        _rootTask = rootTask;
        _blackboard = blackboard;
        _branchTraversal = branchTraversal;
    }

    protected override async Task<HTNPlan?> Process()
    {
        /*
         * Really the best reference for what a HTN looks like is http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter12_Exploring_HTN_Planners_through_Example.pdf
         * It's kinda like a behaviour tree but also can consider multiple actions in sequence.
         *
         * Methods have been renamed to branches
         */

        var decompHistory = new Stack<DecompositionState>();

        // branch traversal record. Whenever we find a new compound task this updates.
        var btrIndex = 0;

        // For some tasks we may do something expensive or want to re-use the planning result.
        // e.g. pathfind to a target before deciding to attack it.
        // Given all of the primitive tasks are singletons we need to store the data somewhere
        // hence we'll store it here.
        var appliedStates = new List<Dictionary<string, object>?>();

        var tasksToProcess = new Queue<HTNTask>();
        var finalPlan = new List<HTNPrimitiveTask>();
        tasksToProcess.Enqueue(_rootTask);

        // How many primitive tasks we've added since last record.
        var primitiveCount = 0;

        while (tasksToProcess.TryDequeue(out var currentTask))
        {
            switch (currentTask)
            {
                case HTNCompoundTask compound:
                    await SuspendIfOutOfTime();

                    // Crescent: decompose depth-first. The branch's tasks go ahead of whatever was still
                    // queued after the compound, so a compound in the middle of a branch is planned in order.
                    var remainingTasks = tasksToProcess.ToArray();
                    tasksToProcess.Clear();

                    if (TryFindSatisfiedMethod(compound, tasksToProcess, _blackboard, ref btrIndex))
                    {
                        foreach (var remaining in remainingTasks)
                        {
                            tasksToProcess.Enqueue(remaining);
                        }

                        // Need to copy worldstate to roll it back
                        // Crescent: the tasks queued after the compound are kept too, so rolling back to it also
                        // brings back the rest of the parent branch.
                        // Don't need to copy finalplan as we can just count how many primitives we've added since last record

                        decompHistory.Push(new DecompositionState()
                        {
                            Blackboard = _blackboard.ShallowClone(),
                            CompoundTask = compound,
                            BranchTraversal = btrIndex,
                            PrimitiveCount = primitiveCount,
                            RemainingTasks = remainingTasks, // Crescent
                        });

                        // TODO: Early out if existing plan is better and save lots of time.
                        // my brain is not working rn AAA

                        primitiveCount = 0;
                        // Reset method traversal
                        btrIndex = 0;
                    }
                    else
                    {
                        RestoreTolastDecomposedTask(decompHistory, tasksToProcess, appliedStates, finalPlan, ref primitiveCount, ref _blackboard, ref btrIndex);
                    }
                    break;
                case HTNPrimitiveTask primitive:
                    if (await WaitAsyncTask(PrimitiveConditionMet(primitive, _blackboard, appliedStates)))
                    {
                        primitiveCount++;
                        finalPlan.Add(primitive);
                    }
                    else
                    {
                        RestoreTolastDecomposedTask(decompHistory, tasksToProcess, appliedStates, finalPlan, ref primitiveCount, ref _blackboard, ref btrIndex);
                    }

                    break;
            }
        }

        if (finalPlan.Count == 0)
        {
            return null;
        }

        var branchTraversalRecord = decompHistory.Reverse().Select(o => o.BranchTraversal).ToList();

        return new HTNPlan(finalPlan, branchTraversalRecord, appliedStates);
    }

    private async Task<bool> PrimitiveConditionMet(HTNPrimitiveTask primitive, NPCBlackboard blackboard, List<Dictionary<string, object>?> appliedStates)
    {
        blackboard.ReadOnly = true;

        foreach (var con in primitive.Preconditions)
        {
            if (con.IsMet(blackboard))
                continue;

            return false;
        }

        var (valid, effects) = await primitive.Operator.Plan(blackboard, Cancellation);

        if (!valid)
            return false;

        blackboard.ReadOnly = false;

        if (effects != null)
        {
            foreach (var (key, value) in effects)
            {
                blackboard.SetValue(key, value);
            }
        }

        appliedStates.Add(effects);

        return true;
    }

    /// <summary>
    /// Goes through each compound task branch and tries to find an appropriate one.
    /// </summary>
    private bool TryFindSatisfiedMethod(HTNCompoundTask compoundId, Queue<HTNTask> tasksToProcess, NPCBlackboard blackboard, ref int mtrIndex)
    {
        var compound = _protoManager.Index<HTNCompoundPrototype>(compoundId.Task);

        for (var i = mtrIndex; i < compound.Branches.Count; i++)
        {
            var branch = compound.Branches[i];
            var isValid = true;

            foreach (var con in branch.Preconditions)
            {
                if (con.IsMet(blackboard))
                    continue;

                isValid = false;
                break;
            }

            if (!isValid)
                continue;

            foreach (var task in branch.Tasks)
            {
                tasksToProcess.Enqueue(task);
            }

            // Crescent: record the branch actually taken, so rolling back moves on to the next branch and
            // the traversal record can be used to compare plans.
            mtrIndex = i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Restores the planner state.
    /// </summary>
    private void RestoreTolastDecomposedTask(
        Stack<DecompositionState> decompHistory,
        Queue<HTNTask> tasksToProcess,
        List<Dictionary<string, object>?> appliedStates,
        List<HTNPrimitiveTask> finalPlan,
        ref int primitiveCount,
        ref NPCBlackboard blackboard,
        ref int mtrIndex)
    {
        tasksToProcess.Clear();

        // No plan found so this will just break normally.
        if (!decompHistory.TryPop(out var lastDecomp))
            return;

        // Increment MTR so next time we try the next method on the compound task.
        mtrIndex = lastDecomp.BranchTraversal + 1;

        var count = finalPlan.Count;
        var reduction = count - primitiveCount;

        // Final plan only has primitive tasks added to it so we can just remove the count we've tracked since the last decomp.
        finalPlan.RemoveRange(reduction, primitiveCount);
        appliedStates.RemoveRange(reduction, primitiveCount);

        primitiveCount = lastDecomp.PrimitiveCount;
        blackboard = lastDecomp.Blackboard;
        tasksToProcess.Enqueue(lastDecomp.CompoundTask);

        // Crescent
        foreach (var remaining in lastDecomp.RemainingTasks)
        {
            tasksToProcess.Enqueue(remaining);
        }
    }

    /// <summary>
    /// Stores the state of an HTN Plan while planning it. This is so we can rollback if a particular branch is unsuitable.
    /// </summary>
    private sealed class DecompositionState
    {
        /// <summary>
        /// Blackboard as at decomposition.
        /// </summary>
        public NPCBlackboard Blackboard = default!;

        /// <summary>
        /// How many primitive tasks we've added since last decompositionstate.
        /// </summary>
        public int PrimitiveCount;

        /// <summary>
        /// The task that owns this decomposition.
        /// </summary>
        public HTNCompoundTask CompoundTask = default!;

        // This may not be necessary for planning but may be useful for debugging so I didn't remove it.
        /// <summary>
        /// Which branch (AKA method) we took of the compound task. Whenever we rollback the decomposition state
        /// this gets incremented by 1 so we check the next method.
        /// </summary>
        public int BranchTraversal;

        /// <summary>
        /// Crescent: the tasks that were queued after the compound task when it was decomposed.
        /// </summary>
        public HTNTask[] RemainingTasks = Array.Empty<HTNTask>();
    }
}
