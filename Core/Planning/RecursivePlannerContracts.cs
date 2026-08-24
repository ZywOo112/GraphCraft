using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Core.Planning;

public sealed record RecursivePlanningLimits
{
	public const int DefaultMaximumDependencyDepth = 64;
	public const int DefaultMaximumBranchExpansions = 4096;

	public RecursivePlanningLimits(
		int maximumDependencyDepth = DefaultMaximumDependencyDepth,
		int maximumBranchExpansions = DefaultMaximumBranchExpansions)
	{
		if (maximumDependencyDepth < 0)
			throw new ArgumentOutOfRangeException(nameof(maximumDependencyDepth));
		if (maximumBranchExpansions <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumBranchExpansions));
		MaximumDependencyDepth = maximumDependencyDepth;
		MaximumBranchExpansions = maximumBranchExpansions;
	}

	public int MaximumDependencyDepth { get; }

	public int MaximumBranchExpansions { get; }
}

public enum RecursivePlanStatus
{
	Success,
	RecipeOutsideTargetSubgraph,
	NoProducerInTargetSubgraph,
	InsufficientTerminalResource,
	TargetSubgraphTruncated,
	AlternativeProducerDeferred,
	DeferredRecipeGroupAllocation,
	BlockedByStation,
	BlockedByCondition,
	UnknownConstraint,
	CycleBoundary,
	PlanningDepthBoundary,
	UserChoiceRequired,
	AlternativeResolutionDeferred,
	NoViableAlternative,
	InvalidExplicitChoice,
	ExplicitChoiceFailed,
	NoViablePlan,
	NoViableResourceAllocation,
	SearchBudgetBoundary
}

public enum PlanningFrontierKind
{
	RecipeOutsideTargetSubgraph,
	NoProducerInTargetSubgraph,
	InsufficientTerminalResource,
	TargetSubgraphTruncated,
	AlternativeProducerDeferred,
	DeferredRecipeGroupAllocation,
	BlockedByStation,
	BlockedByCondition,
	UnknownConstraint,
	CycleBoundary,
	PlanningDepthBoundary,
	InvalidExplicitChoice,
	NoViableResourceAllocation,
	SearchBudgetBoundary
}

public sealed record PlanningPathEntry(
	int Depth,
	RecipeNode Recipe,
	long RequestedOutputQuantity,
	int? IncomingIngredientOrdinal);

public sealed class PlanningFrontier
{
	internal PlanningFrontier(
		PlanningFrontierKind kind,
		RecipeNode blockingRecipe,
		IngredientEdge? requirement,
		ItemNode item,
		long requiredQuantity,
		long availableQuantity,
		long deficitQuantity,
		int depth,
		IReadOnlyList<PlanningPathEntry> dependencyPath,
		IReadOnlyList<RecipeNode> candidateProducers,
		RecipeResolution? resolutionEvidence,
		TargetCraftSubgraph sourceSubgraph)
	{
		Kind = kind;
		BlockingRecipe = blockingRecipe;
		Requirement = requirement;
		Item = item;
		RequiredQuantity = requiredQuantity;
		AvailableQuantity = availableQuantity;
		DeficitQuantity = deficitQuantity;
		Depth = depth;
		DependencyPath = new ReadOnlyCollection<PlanningPathEntry>(dependencyPath.ToArray());
		CandidateProducers = new ReadOnlyCollection<RecipeNode>(candidateProducers
			.OrderBy(recipe => recipe.RecipeIndex)
			.ToArray());
		ResolutionEvidence = resolutionEvidence;
		TargetSubgraphWasTruncated = sourceSubgraph.IsTruncated;
		SubgraphTruncationReason = sourceSubgraph.TruncationReason;
	}

	public PlanningFrontierKind Kind { get; }

	public RecipeNode BlockingRecipe { get; }

	public IngredientEdge? Requirement { get; }

	public ItemNode Item { get; }

	public long RequiredQuantity { get; }

	public long AvailableQuantity { get; }

	public long DeficitQuantity { get; }

	public int Depth { get; }

	public IReadOnlyList<PlanningPathEntry> DependencyPath { get; }

	public IReadOnlyList<RecipeNode> CandidateProducers { get; }

	public RecipeResolution? ResolutionEvidence { get; }

	public bool TargetSubgraphWasTruncated { get; }

	public SubgraphTruncationReason SubgraphTruncationReason { get; }
}

public sealed record PlannedIngredientUse(
	IngredientEdge Ingredient,
	long RequiredQuantity,
	long AvailableBeforeExpansion,
	long DeficitQuantity,
	long AvailableBeforeReservation,
	long RemainingAfterReservation,
	RecipeGroupAllocation? GroupAllocation = null);

public sealed class PlannedCraftStep
{
	internal PlannedCraftStep(
		int sequence,
		int dependencyDepth,
		RecipeNode recipe,
		long requestedOutputQuantity,
		long craftRepetitions,
		long producedQuantity,
		IReadOnlyList<PlannedIngredientUse> ingredients)
	{
		Sequence = sequence;
		DependencyDepth = dependencyDepth;
		Recipe = recipe;
		RequestedOutputQuantity = requestedOutputQuantity;
		CraftRepetitions = craftRepetitions;
		ProducedQuantity = producedQuantity;
		Ingredients = new ReadOnlyCollection<PlannedIngredientUse>(ingredients.ToArray());
	}

	public int Sequence { get; }

	public int DependencyDepth { get; }

	public RecipeNode Recipe { get; }

	public long RequestedOutputQuantity { get; }

	public long CraftRepetitions { get; }

	public long ProducedQuantity { get; }

	public IReadOnlyList<PlannedIngredientUse> Ingredients { get; }

	internal PlannedCraftStep WithSequence(int sequence) => new(
		sequence,
		DependencyDepth,
		Recipe,
		RequestedOutputQuantity,
		CraftRepetitions,
		ProducedQuantity,
		Ingredients);
}

public sealed class RecursiveRecipePlanningRequest
{
	public RecursiveRecipePlanningRequest(
		TargetCraftSubgraph targetSubgraph,
		ResolutionSnapshot resolution,
		RecipeNode selectedRootRecipe,
		int requiredOutputQuantity,
		VirtualResourceLedger ledger,
		RecursivePlanningLimits? limits = null,
		IEnumerable<ExplicitRecipeChoice>? explicitChoices = null)
	{
		ArgumentNullException.ThrowIfNull(targetSubgraph);
		ArgumentNullException.ThrowIfNull(resolution);
		ArgumentNullException.ThrowIfNull(selectedRootRecipe);
		ArgumentNullException.ThrowIfNull(ledger);
		if (requiredOutputQuantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(requiredOutputQuantity));
		if (!ReferenceEquals(targetSubgraph, resolution.SourceSubgraph))
			throw new ArgumentException("The resolution snapshot must belong to the requested target subgraph.", nameof(resolution));
		if (!ReferenceEquals(ledger.SourceSnapshot, resolution.Context.Resources))
			throw new ArgumentException("The virtual ledger must branch from the resource snapshot used by the resolution.", nameof(ledger));

		TargetSubgraph = targetSubgraph;
		Resolution = resolution;
		SelectedRootRecipe = selectedRootRecipe;
		RequiredOutputQuantity = requiredOutputQuantity;
		Ledger = ledger;
		Limits = limits ?? new RecursivePlanningLimits();
		var choices = new Dictionary<AlternativeDecisionId, ExplicitRecipeChoice>();
		foreach (ExplicitRecipeChoice choice in explicitChoices ?? Array.Empty<ExplicitRecipeChoice>()) {
			ArgumentNullException.ThrowIfNull(choice);
			if (!choices.TryAdd(choice.DecisionId, choice))
				throw new ArgumentException($"Duplicate explicit choice for decision {choice.DecisionId}.", nameof(explicitChoices));
		}
		ExplicitChoices = new ReadOnlyDictionary<AlternativeDecisionId, ExplicitRecipeChoice>(choices);
	}

	public TargetCraftSubgraph TargetSubgraph { get; }

	public ResolutionSnapshot Resolution { get; }

	public RecipeNode SelectedRootRecipe { get; }

	public int RequiredOutputQuantity { get; }

	public VirtualResourceLedger Ledger { get; }

	public RecursivePlanningLimits Limits { get; }

	public IReadOnlyDictionary<AlternativeDecisionId, ExplicitRecipeChoice> ExplicitChoices { get; }
}

public sealed class RecursiveRecipePlanResult
{
	internal RecursiveRecipePlanResult(
		RecipeNode selectedRootRecipe,
		int requestedOutputQuantity,
		RecursivePlanStatus status,
		VirtualResourceLedger inputLedger,
		VirtualResourceLedger resultLedger,
		IReadOnlyList<PlannedCraftStep> steps,
		IReadOnlyList<VirtualResourceReservation> reservations,
		IReadOnlyList<VirtualResourceProduction> productions,
		PlanningFrontier? frontier,
		IReadOnlyList<AlternativeDecision>? decisions = null,
		IReadOnlyList<RecipeChoiceApplication>? appliedChoices = null,
		IReadOnlyList<RecipeGroupAllocation>? groupAllocations = null,
		IReadOnlyList<ResourceAllocationDecision>? resourceAllocationDecisions = null,
		PlanningSearchStatistics? searchStatistics = null)
	{
		SelectedRootRecipe = selectedRootRecipe;
		RequestedOutputQuantity = requestedOutputQuantity;
		Status = status;
		InputLedger = inputLedger;
		ResultLedger = resultLedger;
		Steps = new ReadOnlyCollection<PlannedCraftStep>(steps.ToArray());
		Reservations = new ReadOnlyCollection<VirtualResourceReservation>(reservations.ToArray());
		Productions = new ReadOnlyCollection<VirtualResourceProduction>(productions.ToArray());
		Frontier = frontier;
		Decisions = new ReadOnlyCollection<AlternativeDecision>((decisions ?? Array.Empty<AlternativeDecision>()).ToArray());
		AppliedChoices = new ReadOnlyCollection<RecipeChoiceApplication>((appliedChoices ?? Array.Empty<RecipeChoiceApplication>()).ToArray());
		GroupAllocations = new ReadOnlyCollection<RecipeGroupAllocation>((groupAllocations ?? Array.Empty<RecipeGroupAllocation>()).ToArray());
		ResourceAllocationDecisions = new ReadOnlyCollection<ResourceAllocationDecision>((resourceAllocationDecisions ?? Array.Empty<ResourceAllocationDecision>()).ToArray());
		SearchStatistics = searchStatistics ?? new PlanningSearchStatistics(0, 0, false, 0, 0);
	}

	public RecipeNode SelectedRootRecipe { get; }

	public int RequestedOutputQuantity { get; }

	public RecursivePlanStatus Status { get; }

	public bool Succeeded => Status == RecursivePlanStatus.Success;

	public VirtualResourceLedger InputLedger { get; }

	public VirtualResourceLedger ResultLedger { get; }

	public IReadOnlyList<PlannedCraftStep> Steps { get; }

	public IReadOnlyList<VirtualResourceReservation> Reservations { get; }

	public IReadOnlyList<VirtualResourceProduction> Productions { get; }

	public PlanningFrontier? Frontier { get; }

	public IReadOnlyList<AlternativeDecision> Decisions { get; }

	public IReadOnlyList<RecipeChoiceApplication> AppliedChoices { get; }

	public IReadOnlyList<RecipeGroupAllocation> GroupAllocations { get; }

	public IReadOnlyList<ResourceAllocationDecision> ResourceAllocationDecisions { get; }

	public PlanningSearchStatistics SearchStatistics { get; }

	public AlternativeDecision? PendingDecision => Decisions.LastOrDefault(decision => !decision.IsResolved);
}
