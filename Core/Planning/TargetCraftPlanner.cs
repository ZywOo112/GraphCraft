using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Core.Planning;

public readonly record struct RootRecipeDecisionId
{
	public RootRecipeDecisionId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Root recipe decision identity cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }
	public override string ToString() => Value;
}

public sealed record ExplicitRootRecipeChoice
{
	public ExplicitRootRecipeChoice(RootRecipeDecisionId decisionId, RecipeNode selectedRecipe)
	{
		if (string.IsNullOrWhiteSpace(decisionId.Value))
			throw new ArgumentException("Explicit root choice requires a non-default decision identity.", nameof(decisionId));
		ArgumentNullException.ThrowIfNull(selectedRecipe);
		DecisionId = decisionId;
		SelectedRecipe = selectedRecipe;
	}

	public RootRecipeDecisionId DecisionId { get; }
	public RecipeNode SelectedRecipe { get; }
}

public enum RootRecipeDecisionState
{
	AutoSelectedOnlyViable,
	UserChoiceRequired,
	ExplicitlySelected,
	UnresolvedDependencyChoice,
	UnresolvedDeferred,
	NoViableCandidate,
	InvalidExplicitChoice,
	ExplicitChoiceFailed
}

public sealed class RootRecipeCandidateResult
{
	internal RootRecipeCandidateResult(
		RecipeNode recipe,
		AlternativeCandidateStatus status,
		RecursivePlanStatus resultStatus,
		RecursiveRecipePlanResult? planResult)
	{
		Recipe = recipe;
		Status = status;
		ResultStatus = resultStatus;
		PlanResult = planResult;
	}

	public RecipeNode Recipe { get; }
	public AlternativeCandidateStatus Status { get; }
	public RecursivePlanStatus ResultStatus { get; }
	public RecursiveRecipePlanResult? PlanResult { get; }
	public PlanningFrontier? Frontier => PlanResult?.Frontier;
	public bool HasCompleteSuccessfulContinuation => Status == AlternativeCandidateStatus.FullyViable;
}

public sealed class RootRecipeDecision
{
	internal RootRecipeDecision(
		RootRecipeDecisionId decisionId,
		RootRecipeDecisionState state,
		ItemNode target,
		long requestedAdditionalOutputQuantity,
		IReadOnlyList<RootRecipeCandidateResult> candidates,
		RecipeNode? selectedRecipe)
	{
		DecisionId = decisionId;
		State = state;
		Target = target;
		RequestedAdditionalOutputQuantity = requestedAdditionalOutputQuantity;
		Candidates = new ReadOnlyCollection<RootRecipeCandidateResult>(candidates.OrderBy(candidate => candidate.Recipe.RecipeIndex).ToArray());
		UserSelectableCandidates = new ReadOnlyCollection<RootRecipeCandidateResult>(
			state == RootRecipeDecisionState.UserChoiceRequired
				? Candidates.Where(candidate => candidate.HasCompleteSuccessfulContinuation).ToArray()
				: Array.Empty<RootRecipeCandidateResult>());
		SelectedRecipe = selectedRecipe;
	}

	public RootRecipeDecisionId DecisionId { get; }
	public RootRecipeDecisionState State { get; }
	public ItemNode Target { get; }
	public long RequestedAdditionalOutputQuantity { get; }
	public IReadOnlyList<RootRecipeCandidateResult> Candidates { get; }
	public IReadOnlyList<RootRecipeCandidateResult> UserSelectableCandidates { get; }
	public RecipeNode? SelectedRecipe { get; }
	public bool IsResolved => State is RootRecipeDecisionState.AutoSelectedOnlyViable or RootRecipeDecisionState.ExplicitlySelected;
	public int FullyViableCandidateCount => Candidates.Count(candidate => candidate.HasCompleteSuccessfulContinuation);
}

public enum TargetPlanningFrontierKind
{
	InsufficientTerminalResource,
	NoProducerInTargetSubgraph,
	TargetSubgraphTruncated,
	InvalidExplicitRootChoice,
	SearchBudgetBoundary,
	DependencyPlanningBoundary
}

public sealed record TargetPlanningFrontier(
	TargetPlanningFrontierKind Kind,
	ItemNode Target,
	long RequestedAdditionalOutputQuantity,
	IReadOnlyList<RecipeNode> CandidateRootRecipes,
	PlanningFrontier? DependencyFrontier,
	bool TargetSubgraphWasTruncated,
	SubgraphTruncationReason SubgraphTruncationReason);

public sealed class TargetCraftPlanningRequest
{
	public TargetCraftPlanningRequest(
		TargetCraftSubgraph targetSubgraph,
		ResolutionSnapshot resolution,
		ItemNode target,
		int requestedAdditionalOutputQuantity,
		VirtualResourceLedger ledger,
		RecursivePlanningLimits? limits = null,
		ExplicitRootRecipeChoice? explicitRootChoice = null,
		IEnumerable<ExplicitRecipeChoice>? explicitRecipeChoices = null)
	{
		ArgumentNullException.ThrowIfNull(targetSubgraph);
		ArgumentNullException.ThrowIfNull(resolution);
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(ledger);
		if (requestedAdditionalOutputQuantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(requestedAdditionalOutputQuantity));
		if (!ReferenceEquals(targetSubgraph.Target, target))
			throw new ArgumentException("Target must be the canonical target ItemNode of the supplied subgraph.", nameof(target));
		if (!ReferenceEquals(targetSubgraph, resolution.SourceSubgraph))
			throw new ArgumentException("Resolution must belong to the supplied target subgraph.", nameof(resolution));
		if (!ReferenceEquals(ledger.SourceSnapshot, resolution.Context.Resources))
			throw new ArgumentException("Ledger must branch from the Resolution resource snapshot.", nameof(ledger));

		TargetSubgraph = targetSubgraph;
		Resolution = resolution;
		Target = target;
		RequestedAdditionalOutputQuantity = requestedAdditionalOutputQuantity;
		Ledger = ledger;
		Limits = limits ?? new RecursivePlanningLimits();
		ExplicitRootChoice = explicitRootChoice;
		ExplicitRecipeChoices = new ReadOnlyCollection<ExplicitRecipeChoice>((explicitRecipeChoices ?? Array.Empty<ExplicitRecipeChoice>()).ToArray());
	}

	public TargetCraftSubgraph TargetSubgraph { get; }
	public ResolutionSnapshot Resolution { get; }
	public ItemNode Target { get; }
	public int RequestedAdditionalOutputQuantity { get; }
	public VirtualResourceLedger Ledger { get; }
	public RecursivePlanningLimits Limits { get; }
	public ExplicitRootRecipeChoice? ExplicitRootChoice { get; }
	public IReadOnlyList<ExplicitRecipeChoice> ExplicitRecipeChoices { get; }
}

public sealed class TargetCraftPlanResult
{
	internal TargetCraftPlanResult(
		TargetCraftPlanningRequest request,
		RecursivePlanStatus status,
		RecipeNode? selectedRootRecipe,
		RecursiveRecipePlanResult? selectedPlan,
		RootRecipeDecision? rootDecision,
		TargetPlanningFrontier? frontier,
		PlanningSearchStatistics searchStatistics)
	{
		Request = request;
		Status = status;
		SelectedRootRecipe = selectedRootRecipe;
		SelectedPlan = selectedPlan;
		RootDecision = rootDecision;
		Frontier = frontier;
		SearchStatistics = searchStatistics;
	}

	public TargetCraftPlanningRequest Request { get; }
	public ItemNode Target => Request.Target;
	public int RequestedAdditionalOutputQuantity => Request.RequestedAdditionalOutputQuantity;
	public RecursivePlanStatus Status { get; }
	public bool Succeeded => Status == RecursivePlanStatus.Success;
	public RecipeNode? SelectedRootRecipe { get; }
	public RecursiveRecipePlanResult? SelectedPlan { get; }
	public RootRecipeDecision? RootDecision { get; }
	public TargetPlanningFrontier? Frontier { get; }
	public PlanningSearchStatistics SearchStatistics { get; }
	public VirtualResourceLedger InputLedger => Request.Ledger;
	public VirtualResourceLedger ResultLedger => SelectedPlan?.ResultLedger ?? Request.Ledger;
	public IReadOnlyList<PlannedCraftStep> Steps => SelectedPlan?.Steps ?? Array.Empty<PlannedCraftStep>();
	public IReadOnlyList<VirtualResourceReservation> Reservations => SelectedPlan?.Reservations ?? Array.Empty<VirtualResourceReservation>();
	public IReadOnlyList<VirtualResourceProduction> Productions => SelectedPlan?.Productions ?? Array.Empty<VirtualResourceProduction>();
	public IReadOnlyList<AlternativeDecision> RecipeDecisions => SelectedPlan?.Decisions ?? Array.Empty<AlternativeDecision>();
	public IReadOnlyList<RecipeChoiceApplication> AppliedRecipeChoices => SelectedPlan?.AppliedChoices ?? Array.Empty<RecipeChoiceApplication>();
	public IReadOnlyList<RecipeGroupAllocation> GroupAllocations => SelectedPlan?.GroupAllocations ?? Array.Empty<RecipeGroupAllocation>();
	public IReadOnlyList<ResourceAllocationDecision> ResourceAllocationDecisions => SelectedPlan?.ResourceAllocationDecisions ?? Array.Empty<ResourceAllocationDecision>();
	public AlternativeDecision? PendingRecipeDecision => SelectedPlan?.PendingDecision ?? RootDecision?.Candidates
		.Select(candidate => candidate.PlanResult?.PendingDecision)
		.FirstOrDefault(decision => decision is not null);
}

public static class TargetCraftPlanner
{
	public static TargetCraftPlanResult Plan(TargetCraftPlanningRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		RecipeNode[] candidates = request.Target.ProducerRecipes
			.Where(request.TargetSubgraph.RecipeNodes.Contains)
			.OrderBy(recipe => recipe.RecipeIndex)
			.ToArray();
		RootRecipeDecisionId decisionId = new($"root-recipe-choice:target:{request.Target.ItemType}");
		if (candidates.Length == 0)
			return ZeroProducerResult(request);

		if (request.ExplicitRootChoice is { } explicitChoice) {
			if (explicitChoice.DecisionId != decisionId || !candidates.Any(candidate => ReferenceEquals(candidate, explicitChoice.SelectedRecipe))) {
				RootRecipeCandidateResult[] invalidCandidates = candidates.Select(recipe =>
					new RootRecipeCandidateResult(recipe, AlternativeCandidateStatus.NotApplicable, RecursivePlanStatus.InvalidExplicitChoice, null)).ToArray();
				var invalidDecision = new RootRecipeDecision(decisionId, RootRecipeDecisionState.InvalidExplicitChoice, request.Target,
					request.RequestedAdditionalOutputQuantity, invalidCandidates, explicitChoice.SelectedRecipe);
				return Result(request, RecursivePlanStatus.InvalidExplicitChoice, explicitChoice.SelectedRecipe, null, invalidDecision,
					TargetPlanningFrontierKind.InvalidExplicitRootChoice, null, Statistics(request, 0, 0, false, 0, 1, 1, 0, 0));
			}
			RecursiveRecipePlanResult plan = PlanRoot(request, explicitChoice.SelectedRecipe, request.Limits.MaximumBranchExpansions);
			AlternativeCandidateStatus candidateStatus = Classify(plan);
			RootRecipeCandidateResult[] explicitCandidates = candidates.Select(recipe => ReferenceEquals(recipe, explicitChoice.SelectedRecipe)
				? new RootRecipeCandidateResult(recipe, candidateStatus, plan.Status, plan)
				: new RootRecipeCandidateResult(recipe, AlternativeCandidateStatus.NotApplicable, RecursivePlanStatus.InvalidExplicitChoice, null)).ToArray();
			bool provenFailure = candidateStatus is AlternativeCandidateStatus.Failed or AlternativeCandidateStatus.Blocked;
			RootRecipeDecisionState state = plan.Succeeded
				? RootRecipeDecisionState.ExplicitlySelected
				: provenFailure ? RootRecipeDecisionState.ExplicitChoiceFailed
				: plan.Status == RecursivePlanStatus.UserChoiceRequired ? RootRecipeDecisionState.ExplicitlySelected : RootRecipeDecisionState.UnresolvedDeferred;
			var decision = new RootRecipeDecision(decisionId, state, request.Target, request.RequestedAdditionalOutputQuantity,
				explicitCandidates, explicitChoice.SelectedRecipe);
			RecursivePlanStatus status = provenFailure ? RecursivePlanStatus.ExplicitChoiceFailed : plan.Status;
			return Result(request, status, explicitChoice.SelectedRecipe, plan, decision, MapFrontier(status), plan.Frontier,
				AggregateStatistics(request, new[] { plan }, rootRecipeBranches: 0, rootDecisionCount: candidates.Length > 1 ? 1 : 0));
		}

		var evaluated = new List<RootRecipeCandidateResult>();
		var plans = new List<RecursiveRecipePlanResult>();
		int rootRecipeBranches = 0;
		bool rootBudgetExhausted = false;
		foreach (RecipeNode candidate in candidates) {
			if (candidates.Length > 1) {
				if (rootRecipeBranches + plans.Sum(plan => plan.SearchStatistics.ExpandedBranches) >= request.Limits.MaximumBranchExpansions) {
					rootBudgetExhausted = true;
					evaluated.Add(new RootRecipeCandidateResult(candidate, AlternativeCandidateStatus.Deferred, RecursivePlanStatus.SearchBudgetBoundary, null));
					break;
				}
				rootRecipeBranches++;
			}
			int remaining = request.Limits.MaximumBranchExpansions - rootRecipeBranches - plans.Sum(plan => plan.SearchStatistics.ExpandedBranches);
			if (remaining <= 0) {
				rootBudgetExhausted = true;
				evaluated.Add(new RootRecipeCandidateResult(candidate, AlternativeCandidateStatus.Deferred, RecursivePlanStatus.SearchBudgetBoundary, null));
				break;
			}
			RecursiveRecipePlanResult plan = PlanRoot(request, candidate, remaining);
			plans.Add(plan);
			evaluated.Add(new RootRecipeCandidateResult(candidate, Classify(plan), plan.Status, plan));
			if (plan.SearchStatistics.BudgetExhausted)
				rootBudgetExhausted = true;
		}

		RootRecipeCandidateResult[] viable = evaluated.Where(candidate => candidate.HasCompleteSuccessfulContinuation).ToArray();
		if (viable.Length >= 2) {
			var decision = new RootRecipeDecision(decisionId, RootRecipeDecisionState.UserChoiceRequired, request.Target,
				request.RequestedAdditionalOutputQuantity, evaluated, null);
			return new TargetCraftPlanResult(request, RecursivePlanStatus.UserChoiceRequired, null, null, decision, null,
				AggregateStatistics(request, plans, rootRecipeBranches, candidates.Length > 1 ? 1 : 0, rootBudgetExhausted));
		}
		if (viable.Length == 1) {
			RootRecipeCandidateResult selected = viable[0];
			var decision = new RootRecipeDecision(decisionId, RootRecipeDecisionState.AutoSelectedOnlyViable, request.Target,
				request.RequestedAdditionalOutputQuantity, evaluated, selected.Recipe);
			return new TargetCraftPlanResult(request, RecursivePlanStatus.Success, selected.Recipe, selected.PlanResult, decision, null,
				AggregateStatistics(request, plans, rootRecipeBranches, candidates.Length > 1 ? 1 : 0, rootBudgetExhausted));
		}

		RootRecipeCandidateResult? actionable = evaluated.FirstOrDefault(candidate => candidate.ResultStatus == RecursivePlanStatus.UserChoiceRequired);
		if (actionable is not null) {
			var decision = new RootRecipeDecision(decisionId, RootRecipeDecisionState.UnresolvedDependencyChoice, request.Target,
				request.RequestedAdditionalOutputQuantity, evaluated, actionable.Recipe);
			return new TargetCraftPlanResult(request, RecursivePlanStatus.UserChoiceRequired, actionable.Recipe, actionable.PlanResult, decision, null,
				AggregateStatistics(request, plans, rootRecipeBranches, candidates.Length > 1 ? 1 : 0, rootBudgetExhausted));
		}

		RecursivePlanStatus aggregateStatus = AggregateFailureStatus(evaluated, rootBudgetExhausted);
		RootRecipeCandidateResult? representative = evaluated.OrderBy(candidate => FailureRank(candidate.ResultStatus)).ThenBy(candidate => candidate.Recipe.RecipeIndex).FirstOrDefault();
		RootRecipeDecisionState failureState = IsDeferred(aggregateStatus)
			? RootRecipeDecisionState.UnresolvedDeferred
			: RootRecipeDecisionState.NoViableCandidate;
		var failedDecision = new RootRecipeDecision(decisionId, failureState, request.Target,
			request.RequestedAdditionalOutputQuantity, evaluated, null);
		return Result(request, aggregateStatus, null, representative?.PlanResult, failedDecision, MapFrontier(aggregateStatus),
			representative?.PlanResult?.Frontier,
			AggregateStatistics(request, plans, rootRecipeBranches, candidates.Length > 1 ? 1 : 0, rootBudgetExhausted));
	}

	private static RecursiveRecipePlanResult PlanRoot(TargetCraftPlanningRequest request, RecipeNode root, int maximumBranches) =>
		GraphConstrainedCraftPlanner.Plan(new RecursiveRecipePlanningRequest(
			request.TargetSubgraph,
			request.Resolution,
			root,
			request.RequestedAdditionalOutputQuantity,
			request.Ledger,
			new RecursivePlanningLimits(request.Limits.MaximumDependencyDepth, Math.Max(1, maximumBranches)),
			request.ExplicitRecipeChoices));

	private static TargetCraftPlanResult ZeroProducerResult(TargetCraftPlanningRequest request)
	{
		RecursivePlanStatus status;
		TargetPlanningFrontierKind kind;
		if (request.TargetSubgraph.IsTruncated) {
			status = RecursivePlanStatus.TargetSubgraphTruncated;
			kind = TargetPlanningFrontierKind.TargetSubgraphTruncated;
		}
		else if (request.Target.Classification == ItemClassification.Atomic) {
			status = RecursivePlanStatus.InsufficientTerminalResource;
			kind = TargetPlanningFrontierKind.InsufficientTerminalResource;
		}
		else {
			status = RecursivePlanStatus.NoProducerInTargetSubgraph;
			kind = TargetPlanningFrontierKind.NoProducerInTargetSubgraph;
		}
		return Result(request, status, null, null, null, kind, null, Statistics(request, 0, 0, false, 0, 1, 0, 0, 0));
	}

	private static TargetCraftPlanResult Result(TargetCraftPlanningRequest request, RecursivePlanStatus status,
		RecipeNode? selectedRoot, RecursiveRecipePlanResult? plan, RootRecipeDecision? decision,
		TargetPlanningFrontierKind? frontierKind, PlanningFrontier? dependencyFrontier, PlanningSearchStatistics statistics)
	{
		TargetPlanningFrontier? frontier = frontierKind is null ? null : new TargetPlanningFrontier(
			frontierKind.Value,
			request.Target,
			request.RequestedAdditionalOutputQuantity,
			request.Target.ProducerRecipes.Where(request.TargetSubgraph.RecipeNodes.Contains).OrderBy(recipe => recipe.RecipeIndex).ToArray(),
			dependencyFrontier,
			request.TargetSubgraph.IsTruncated,
			request.TargetSubgraph.TruncationReason);
		return new TargetCraftPlanResult(request, status, selectedRoot, plan, decision, frontier, statistics);
	}

	private static PlanningSearchStatistics AggregateStatistics(TargetCraftPlanningRequest request,
		IReadOnlyList<RecursiveRecipePlanResult> plans, int rootRecipeBranches, int rootDecisionCount, bool forceBudgetExhausted = false)
	{
		return Statistics(
			request,
			rootRecipeBranches + plans.Sum(plan => plan.SearchStatistics.ExpandedBranches),
			plans.Sum(plan => plan.SearchStatistics.RecipeBranchExpansions) + rootRecipeBranches,
			forceBudgetExhausted || plans.Any(plan => plan.SearchStatistics.BudgetExhausted),
			plans.Sum(plan => plan.SearchStatistics.SuccessfulPlanCount),
			plans.Sum(plan => plan.SearchStatistics.FailedPlanCount),
			plans.Sum(plan => plan.SearchStatistics.RecipeDecisionCount) + rootDecisionCount,
			plans.Sum(plan => plan.SearchStatistics.ResourceAllocationBranchExpansions),
			plans.Sum(plan => plan.SearchStatistics.ResourceAllocationDecisionCount));
	}

	private static PlanningSearchStatistics Statistics(TargetCraftPlanningRequest request, int expanded, int recipeBranches,
		bool exhausted, int successes, int failures, int recipeDecisions, int resourceBranches, int resourceDecisions) =>
		new(expanded, request.Limits.MaximumBranchExpansions, exhausted, successes, failures, recipeBranches, resourceBranches,
			failures, recipeDecisions, resourceDecisions);

	private static AlternativeCandidateStatus Classify(RecursiveRecipePlanResult plan)
	{
		if (plan.Succeeded)
			return AlternativeCandidateStatus.FullyViable;
		return plan.Status switch
		{
			RecursivePlanStatus.UserChoiceRequired => AlternativeCandidateStatus.RequiresUserChoice,
			RecursivePlanStatus.BlockedByStation or RecursivePlanStatus.BlockedByCondition => AlternativeCandidateStatus.Blocked,
			RecursivePlanStatus.TargetSubgraphTruncated or RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.CycleBoundary
				or RecursivePlanStatus.PlanningDepthBoundary or RecursivePlanStatus.AlternativeResolutionDeferred
				or RecursivePlanStatus.SearchBudgetBoundary => AlternativeCandidateStatus.Deferred,
			RecursivePlanStatus.InvalidExplicitChoice => AlternativeCandidateStatus.NotApplicable,
			_ => AlternativeCandidateStatus.Failed
		};
	}

	private static RecursivePlanStatus AggregateFailureStatus(IReadOnlyList<RootRecipeCandidateResult> candidates, bool budgetExhausted)
	{
		if (budgetExhausted || candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.SearchBudgetBoundary))
			return RecursivePlanStatus.SearchBudgetBoundary;
		if (candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.TargetSubgraphTruncated))
			return RecursivePlanStatus.TargetSubgraphTruncated;
		if (candidates.Count == 1)
			return candidates[0].ResultStatus;
		if (candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.UnknownConstraint))
			return RecursivePlanStatus.UnknownConstraint;
		if (candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.CycleBoundary))
			return RecursivePlanStatus.CycleBoundary;
		if (candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.PlanningDepthBoundary))
			return RecursivePlanStatus.PlanningDepthBoundary;
		if (candidates.Any(candidate => IsDeferred(candidate.ResultStatus)))
			return RecursivePlanStatus.AlternativeResolutionDeferred;
		if (candidates.Count > 1)
			return RecursivePlanStatus.NoViableAlternative;
		return RecursivePlanStatus.NoViablePlan;
	}

	private static TargetPlanningFrontierKind? MapFrontier(RecursivePlanStatus status) => status switch
	{
		RecursivePlanStatus.InsufficientTerminalResource => TargetPlanningFrontierKind.InsufficientTerminalResource,
		RecursivePlanStatus.NoProducerInTargetSubgraph => TargetPlanningFrontierKind.NoProducerInTargetSubgraph,
		RecursivePlanStatus.TargetSubgraphTruncated => TargetPlanningFrontierKind.TargetSubgraphTruncated,
		RecursivePlanStatus.InvalidExplicitChoice => TargetPlanningFrontierKind.InvalidExplicitRootChoice,
		RecursivePlanStatus.SearchBudgetBoundary => TargetPlanningFrontierKind.SearchBudgetBoundary,
		RecursivePlanStatus.Success or RecursivePlanStatus.UserChoiceRequired => null,
		_ => TargetPlanningFrontierKind.DependencyPlanningBoundary
	};

	private static bool IsDeferred(RecursivePlanStatus status) => status is RecursivePlanStatus.TargetSubgraphTruncated
		or RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.CycleBoundary or RecursivePlanStatus.PlanningDepthBoundary
		or RecursivePlanStatus.AlternativeResolutionDeferred or RecursivePlanStatus.SearchBudgetBoundary;

	private static int FailureRank(RecursivePlanStatus status) => status switch
	{
		RecursivePlanStatus.SearchBudgetBoundary => 0,
		RecursivePlanStatus.TargetSubgraphTruncated => 1,
		RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.CycleBoundary or RecursivePlanStatus.PlanningDepthBoundary => 2,
		RecursivePlanStatus.BlockedByStation or RecursivePlanStatus.BlockedByCondition => 3,
		_ => 4
	};
}
