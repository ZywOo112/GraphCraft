using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Planning;

public readonly record struct AlternativeDecisionId
{
	public AlternativeDecisionId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Alternative decision identity cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }

	public override string ToString() => Value;
}

public sealed class ExplicitRecipeChoice
{
	public ExplicitRecipeChoice(AlternativeDecisionId decisionId, RecipeNode selectedRecipe)
	{
		if (string.IsNullOrWhiteSpace(decisionId.Value))
			throw new ArgumentException("Explicit choice requires a non-default decision identity.", nameof(decisionId));
		ArgumentNullException.ThrowIfNull(selectedRecipe);
		DecisionId = decisionId;
		SelectedRecipe = selectedRecipe;
	}

	public AlternativeDecisionId DecisionId { get; }

	public RecipeNode SelectedRecipe { get; }
}

public enum AlternativeCandidateStatus
{
	FullyViable,
	RequiresUserChoice,
	Failed,
	Blocked,
	Deferred,
	NotApplicable
}

public enum AlternativeDecisionState
{
	UserChoiceRequired,
	AutoSelectedOnlyViable,
	ExplicitlySelected,
	UnresolvedDeferred,
	NoViableCandidate,
	InvalidExplicitChoice,
	ExplicitChoiceFailed
}

public enum RecipeChoiceApplicationKind
{
	AutoSelectedOnlyViable,
	ExplicitUserChoice
}

public sealed record RecipeChoiceApplication(
	AlternativeDecisionId DecisionId,
	RecipeNode SelectedRecipe,
	RecipeChoiceApplicationKind Kind);

public sealed class AlternativeCandidateResult
{
	internal AlternativeCandidateResult(
		RecipeNode recipe,
		AlternativeCandidateStatus status,
		long requestedOutputQuantity,
		long craftRepetitions,
		long producedQuantity,
		RecursivePlanStatus resultStatus,
		VirtualResourceLedger? resultLedger,
		IReadOnlyList<PlannedCraftStep> previewSteps,
		IReadOnlyList<VirtualResourceReservation> previewReservations,
		IReadOnlyList<VirtualResourceProduction> previewProductions,
		PlanningFrontier? frontier,
		IReadOnlyList<AlternativeDecision> decisions,
		AlternativeDecision? nestedDecision,
		IReadOnlyList<RecipeChoiceApplication> appliedChoices)
	{
		Recipe = recipe;
		Status = status;
		RequestedOutputQuantity = requestedOutputQuantity;
		CraftRepetitions = craftRepetitions;
		ProducedQuantity = producedQuantity;
		ResultStatus = resultStatus;
		ResultLedger = resultLedger;
		PreviewSteps = new ReadOnlyCollection<PlannedCraftStep>(previewSteps.ToArray());
		PreviewReservations = new ReadOnlyCollection<VirtualResourceReservation>(previewReservations.ToArray());
		PreviewProductions = new ReadOnlyCollection<VirtualResourceProduction>(previewProductions.ToArray());
		Frontier = frontier;
		Decisions = new ReadOnlyCollection<AlternativeDecision>(decisions.ToArray());
		NestedDecision = nestedDecision;
		AppliedChoices = new ReadOnlyCollection<RecipeChoiceApplication>(appliedChoices.ToArray());
	}

	public RecipeNode Recipe { get; }

	public AlternativeCandidateStatus Status { get; }

	public long RequestedOutputQuantity { get; }

	public long CraftRepetitions { get; }

	public long ProducedQuantity { get; }

	public RecursivePlanStatus ResultStatus { get; }

	public VirtualResourceLedger? ResultLedger { get; }

	public IReadOnlyList<PlannedCraftStep> PreviewSteps { get; }

	public IReadOnlyList<VirtualResourceReservation> PreviewReservations { get; }

	public IReadOnlyList<VirtualResourceProduction> PreviewProductions { get; }

	public PlanningFrontier? Frontier { get; }

	public IReadOnlyList<AlternativeDecision> Decisions { get; }

	public AlternativeDecision? NestedDecision { get; }

	public IReadOnlyList<RecipeChoiceApplication> AppliedChoices { get; }

	public bool IsProvenUnavailable => Status is AlternativeCandidateStatus.Failed or AlternativeCandidateStatus.Blocked;
}

public sealed class AlternativeDecision
{
	internal AlternativeDecision(
		AlternativeDecisionId decisionId,
		AlternativeDecisionState state,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long requiredQuantity,
		long availableQuantity,
		long deficitQuantity,
		int depth,
		IReadOnlyList<PlanningPathEntry> dependencyPath,
		IReadOnlyList<AlternativeCandidateResult> candidates,
		RecipeNode? selectedRecipe)
	{
		DecisionId = decisionId;
		State = state;
		ConsumingRecipe = consumingRecipe;
		Requirement = requirement;
		Item = requirement.Source;
		RequiredQuantity = requiredQuantity;
		AvailableQuantity = availableQuantity;
		DeficitQuantity = deficitQuantity;
		Depth = depth;
		DependencyPath = new ReadOnlyCollection<PlanningPathEntry>(dependencyPath.ToArray());
		Candidates = new ReadOnlyCollection<AlternativeCandidateResult>(candidates
			.OrderBy(candidate => candidate.Recipe.RecipeIndex)
			.ToArray());
		UserSelectableCandidates = new ReadOnlyCollection<AlternativeCandidateResult>(
			State == AlternativeDecisionState.UserChoiceRequired
				? Candidates.Where(candidate => candidate.Status == AlternativeCandidateStatus.FullyViable).ToArray()
				: Array.Empty<AlternativeCandidateResult>());
		SelectedRecipe = selectedRecipe;
	}

	public AlternativeDecisionId DecisionId { get; }

	public AlternativeDecisionState State { get; }

	public RecipeNode ConsumingRecipe { get; }

	public IngredientEdge Requirement { get; }

	public ItemNode Item { get; }

	public long RequiredQuantity { get; }

	public long AvailableQuantity { get; }

	public long DeficitQuantity { get; }

	public int Depth { get; }

	public IReadOnlyList<PlanningPathEntry> DependencyPath { get; }

	public IReadOnlyList<AlternativeCandidateResult> Candidates { get; }

	public IReadOnlyList<AlternativeCandidateResult> UserSelectableCandidates { get; }

	public RecipeNode? SelectedRecipe { get; }

	public bool IsResolved => State is AlternativeDecisionState.AutoSelectedOnlyViable or AlternativeDecisionState.ExplicitlySelected;

	public int FullyViableCandidateCount => Candidates.Count(candidate => candidate.Status == AlternativeCandidateStatus.FullyViable);
}
