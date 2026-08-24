using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Core.Planning;

/// <summary>
/// Compatibility entry point retained for callers from Milestones 4.1-4.3.
/// M4.4 planning is implemented by <see cref="GraphConstrainedCraftPlanner"/>.
/// </summary>
public static class RecursiveSinglePathPlanner
{
	public static RecursiveRecipePlanResult Plan(RecursiveRecipePlanningRequest request) =>
		GraphConstrainedCraftPlanner.Plan(request);
}

/// <summary>
/// Bounded, deterministic, graph-constrained planning search. The planner only
/// follows recipes and concrete Recipe Group members present in the supplied
/// TargetCraftSubgraph. It never reads Terraria's mutable recipe registry.
/// </summary>
public static class GraphConstrainedCraftPlanner
{
	public static RecursiveRecipePlanResult Plan(RecursiveRecipePlanningRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		var context = new SearchContext(request);
		if (!context.IncludedRecipes.Contains(request.SelectedRootRecipe)) {
			PlanningFrontier frontier = context.Frontier(
				PlanningFrontierKind.RecipeOutsideTargetSubgraph,
				request.SelectedRootRecipe,
				null,
				request.SelectedRootRecipe.Result,
				request.RequiredOutputQuantity,
				request.Ledger.GetBalance(request.SelectedRootRecipe.Result.ItemType).RemainingQuantity,
				request.RequiredOutputQuantity,
				0,
				Array.Empty<PlanningPathEntry>(),
				Array.Empty<RecipeNode>(),
				null);
			return FailureResult(context, Outcome.Failure(SearchState.Create(request.Ledger), RecursivePlanStatus.RecipeOutsideTargetSubgraph, frontier));
		}

		IReadOnlyList<Outcome> outcomes = PlanRecipe(
			context,
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			SearchState.Create(request.Ledger),
			0,
			null,
			Array.Empty<PlanningPathEntry>());
		return Aggregate(context, outcomes);
	}

	private static IReadOnlyList<Outcome> PlanRecipe(
		SearchContext context,
		RecipeNode recipe,
		long requestedOutputQuantity,
		SearchState input,
		int depth,
		int? incomingIngredientOrdinal,
		IReadOnlyList<PlanningPathEntry> dependencyPath)
	{
		var entry = new PlanningPathEntry(depth, recipe, requestedOutputQuantity, incomingIngredientOrdinal);
		PlanningPathEntry[] path = dependencyPath.Append(entry).ToArray();
		if (depth > context.Request.Limits.MaximumDependencyDepth)
			return One(context.Fail(input, RecursivePlanStatus.PlanningDepthBoundary, PlanningFrontierKind.PlanningDepthBoundary, recipe, null, recipe.Result, requestedOutputQuantity, requestedOutputQuantity, depth, path));
		if (dependencyPath.Any(parent => ReferenceEquals(parent.Recipe, recipe)))
			return One(context.Fail(input, RecursivePlanStatus.CycleBoundary, PlanningFrontierKind.CycleBoundary, recipe, null, recipe.Result, requestedOutputQuantity, requestedOutputQuantity, depth, path, new[] { recipe }));

		RecipeResolution resolution = context.Request.Resolution.GetRecipeResolution(recipe);
		(RecursivePlanStatus Status, PlanningFrontierKind Kind)? blocked = resolution.State switch
		{
			RecipeLocalState.BlockedByStation => (RecursivePlanStatus.BlockedByStation, PlanningFrontierKind.BlockedByStation),
			RecipeLocalState.BlockedByCondition => (RecursivePlanStatus.BlockedByCondition, PlanningFrontierKind.BlockedByCondition),
			RecipeLocalState.UnknownConstraint => (RecursivePlanStatus.UnknownConstraint, PlanningFrontierKind.UnknownConstraint),
			RecipeLocalState.ReadyNow or RecipeLocalState.NeedsIngredients => null,
			_ => throw new InvalidOperationException($"Unsupported recipe local state {resolution.State}.")
		};
		if (blocked is { } boundary)
			return One(context.Fail(input, boundary.Status, boundary.Kind, recipe, null, recipe.Result, requestedOutputQuantity, requestedOutputQuantity, depth, path, resolutionEvidence: resolution));

		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutputQuantity);
		IngredientEdge[] ingredients = recipe.Ingredients
			.OrderBy(ingredient => ingredient.Ordinal)
			.ThenBy(ingredient => ingredient.Source.ItemType)
			.ToArray();
		return ProcessIngredient(0, input, Array.Empty<PlannedIngredientUse>());

		IReadOnlyList<Outcome> ProcessIngredient(int index, SearchState state, IReadOnlyList<PlannedIngredientUse> ingredientUses)
		{
			if (index == ingredients.Length) {
				VirtualProductionCredit credit = state.Ledger.CreditPlannedProduction(recipe.Result.ItemType, quantities.ProducedQuantity);
				SearchState completed = state.WithProduction(credit).WithStep(new PlannedCraftStep(
					state.Steps.Count,
					depth,
					recipe,
					requestedOutputQuantity,
					quantities.CraftRepetitions,
					quantities.ProducedQuantity,
					ingredientUses));
				return One(Outcome.Success(completed));
			}

			IngredientEdge ingredient = ingredients[index];
			long required = quantities.ScaleIngredient(ingredient);
			IReadOnlyList<Outcome> ingredientOutcomes = ingredient.AcceptsRecipeGroup
				? SatisfyGroup(context, recipe, ingredient, required, state, depth, path)
				: SatisfyExact(context, recipe, ingredient, required, state, depth, path);
			var results = new List<Outcome>();
			foreach (Outcome outcome in ingredientOutcomes) {
				if (!outcome.Succeeded) {
					results.Add(outcome);
					continue;
				}
				PlannedIngredientUse use = outcome.IngredientUse
					?? throw new InvalidOperationException("A satisfied ingredient omitted planning evidence.");
				results.AddRange(ProcessIngredient(index + 1, outcome.State, ingredientUses.Append(use).ToArray()));
			}
			return results;
		}
	}

	private static IReadOnlyList<Outcome> SatisfyExact(
		SearchContext context,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long required,
		SearchState input,
		int depth,
		IReadOnlyList<PlanningPathEntry> path)
	{
		long availableBeforeExpansion = input.Ledger.GetBalance(requirement.Source.ItemType).RemainingQuantity;
		long deficit = Math.Max(0, required - availableBeforeExpansion);
		IReadOnlyList<Outcome> expanded = deficit == 0
			? One(Outcome.Success(input))
			: ProduceExact(context, consumingRecipe, requirement, required, availableBeforeExpansion, deficit, input, depth, path);
		var results = new List<Outcome>();
		foreach (Outcome outcome in expanded) {
			if (!outcome.Succeeded) {
				results.Add(outcome);
				continue;
			}
			long beforeReservation = outcome.State.Ledger.GetBalance(requirement.Source.ItemType).RemainingQuantity;
			VirtualReservationAttempt reservation = outcome.State.Ledger.TryReserveExact(requirement.Source.ItemType, required);
			if (!reservation.Succeeded) {
				results.Add(context.Fail(outcome.State, RecursivePlanStatus.NoViablePlan, PlanningFrontierKind.InsufficientTerminalResource,
					consumingRecipe, requirement, requirement.Source, required, reservation.ShortageQuantity, depth, path));
				continue;
			}
			VirtualResourceReservation evidence = reservation.Reservation ?? throw new InvalidOperationException("Successful reservation omitted evidence.");
			results.Add(Outcome.Success(
				outcome.State.WithReservation(reservation.Ledger, evidence),
				new PlannedIngredientUse(requirement, required, availableBeforeExpansion, deficit, beforeReservation, evidence.RemainingAfter)));
		}
		return results;
	}

	private static IReadOnlyList<Outcome> ProduceExact(
		SearchContext context,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long required,
		long available,
		long deficit,
		SearchState input,
		int depth,
		IReadOnlyList<PlanningPathEntry> path)
	{
		RecipeNode[] candidates = requirement.Source.ProducerRecipes
			.Where(context.IncludedRecipes.Contains)
			.OrderBy(recipe => recipe.RecipeIndex)
			.ToArray();
		if (candidates.Length == 0) {
			(RecursivePlanStatus status, PlanningFrontierKind kind) = context.ZeroProducerBoundary(requirement.Source);
			return One(context.Fail(input, status, kind, consumingRecipe, requirement, requirement.Source, required, deficit, depth, path));
		}
		if (candidates.Length == 1)
			return PlanRecipe(context, candidates[0], deficit, input, depth + 1, requirement.Ordinal, path);

		AlternativeDecisionId id = CreateDecisionId(consumingRecipe, requirement, path);
		if (context.Request.ExplicitChoices.TryGetValue(id, out ExplicitRecipeChoice? explicitChoice)) {
			RecipeNode? selected = candidates.FirstOrDefault(candidate => ReferenceEquals(candidate, explicitChoice.SelectedRecipe));
			if (selected is null) {
				AlternativeTrace invalid = new(id, consumingRecipe, requirement, required, available, deficit, depth, path, candidates, explicitChoice.SelectedRecipe, true, true);
				SearchState invalidState = input.WithAlternative(invalid);
				PlanningFrontier frontier = context.Frontier(PlanningFrontierKind.InvalidExplicitChoice, consumingRecipe, requirement, requirement.Source, required, available, deficit, depth, path, candidates, null);
				return One(Outcome.Failure(invalidState, RecursivePlanStatus.InvalidExplicitChoice, frontier));
			}
			AlternativeTrace trace = new(id, consumingRecipe, requirement, required, available, deficit, depth, path, candidates, selected, true, false);
			return PlanRecipe(context, selected, deficit, input.WithAlternative(trace), depth + 1, requirement.Ordinal, path);
		}

		var results = new List<Outcome>();
		foreach (RecipeNode candidate in candidates) {
			if (!context.TryExpand(out PlanningSearchStatistics _)) {
				PlanningFrontier frontier = context.Frontier(PlanningFrontierKind.SearchBudgetBoundary, consumingRecipe, requirement, requirement.Source, required, available, deficit, depth, path, candidates, null);
				results.Add(Outcome.Failure(input, RecursivePlanStatus.SearchBudgetBoundary, frontier));
				break;
			}
			AlternativeTrace trace = new(id, consumingRecipe, requirement, required, available, deficit, depth, path, candidates, candidate, false, false);
			results.AddRange(PlanRecipe(context, candidate, deficit, input.WithAlternative(trace), depth + 1, requirement.Ordinal, path));
		}
		return results;
	}

	private static IReadOnlyList<Outcome> SatisfyGroup(
		SearchContext context,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long required,
		SearchState input,
		int depth,
		IReadOnlyList<PlanningPathEntry> path)
	{
		int[] accepted = requirement.AcceptedRecipeGroups
			.SelectMany(group => group.AcceptedItemTypes)
			.Distinct()
			.OrderBy(itemType => itemType)
			.ToArray();
		if (accepted.Length == 0)
			return One(context.Fail(input, RecursivePlanStatus.NoViableResourceAllocation, PlanningFrontierKind.NoViableResourceAllocation,
				consumingRecipe, requirement, requirement.Source, required, required, depth, path));

		ResourceAllocationDecisionId decisionId = CreateResourceDecisionId(consumingRecipe, requirement, path);
		var results = new List<Outcome>();
		for (int rotation = 0; rotation < accepted.Length; rotation++) {
			if (accepted.Length > 1 && !context.TryExpand(out PlanningSearchStatistics _)) {
				PlanningFrontier frontier = context.Frontier(PlanningFrontierKind.SearchBudgetBoundary, consumingRecipe, requirement, requirement.Source,
					required, AggregateAvailable(input.Ledger, accepted), required, depth, path, Array.Empty<RecipeNode>(), null);
				results.Add(Outcome.Failure(input, RecursivePlanStatus.SearchBudgetBoundary, frontier));
				break;
			}
			int[] priority = accepted.Skip(rotation).Concat(accepted.Take(rotation)).ToArray();
			results.AddRange(Allocate(0, required, input, new List<ConcreteItemAllocation>()));

			IReadOnlyList<Outcome> Allocate(int index, long remaining, SearchState state, List<ConcreteItemAllocation> allocations)
			{
				if (remaining == 0)
					return CommitGroupAllocation(state, allocations);
				if (index == priority.Length) {
					PlanningFrontier frontier = context.Frontier(PlanningFrontierKind.NoViableResourceAllocation, consumingRecipe, requirement, requirement.Source,
						required, AggregateAvailable(state.Ledger, accepted), remaining, depth, path, Array.Empty<RecipeNode>(), null);
					return One(Outcome.Failure(state, RecursivePlanStatus.NoViableResourceAllocation, frontier));
				}

				int itemType = priority[index];
				long currentAvailable = state.Ledger.GetBalance(itemType).RemainingQuantity;
				long takeExisting = Math.Min(remaining, currentAvailable);
				long afterExisting = remaining - takeExisting;
				long laterAvailable = priority.Skip(index + 1).Sum(type => Math.Max(0, state.Ledger.GetBalance(type).RemainingQuantity));
				long productionNeeded = Math.Max(0, afterExisting - laterAvailable);
				IReadOnlyList<Outcome> productionOutcomes;
				if (productionNeeded == 0) {
					productionOutcomes = One(Outcome.Success(state));
				}
				else if (!context.IncludedItems.TryGetValue(itemType, out ItemNode? member)) {
					productionOutcomes = Array.Empty<Outcome>();
				}
				else {
					RecipeNode[] producers = member.ProducerRecipes.Where(context.IncludedRecipes.Contains).OrderBy(recipe => recipe.RecipeIndex).ToArray();
					if (producers.Length == 0) {
						productionOutcomes = Array.Empty<Outcome>();
					}
					else if (producers.Length == 1) {
						productionOutcomes = PlanRecipe(context, producers[0], productionNeeded, state, depth + 1, requirement.Ordinal, path);
					}
					else {
						productionOutcomes = ProduceGroupMemberAlternatives(context, consumingRecipe, requirement, required, member, productionNeeded, state, depth, path, producers);
					}
				}

				var branchResults = new List<Outcome>();
				foreach (Outcome produced in productionOutcomes) {
					if (!produced.Succeeded) {
						branchResults.Add(produced);
						continue;
					}
					long allocatable = Math.Min(remaining, produced.State.Ledger.GetBalance(itemType).RemainingQuantity);
					var nextAllocations = new List<ConcreteItemAllocation>(allocations);
					if (allocatable > 0)
						nextAllocations.Add(new ConcreteItemAllocation(itemType, allocatable));
					branchResults.AddRange(Allocate(index + 1, remaining - allocatable, produced.State, nextAllocations));
				}
				if (productionOutcomes.Count == 0)
					branchResults.AddRange(Allocate(index + 1, remaining - takeExisting, state, Append(allocations, itemType, takeExisting)));
				return branchResults;
			}

			IReadOnlyList<Outcome> CommitGroupAllocation(SearchState state, IReadOnlyList<ConcreteItemAllocation> rawAllocations)
			{
				ConcreteItemAllocation[] concrete = rawAllocations
					.GroupBy(allocation => allocation.ItemType)
					.Select(group => new ConcreteItemAllocation(group.Key, group.Sum(allocation => allocation.Quantity)))
					.OrderBy(allocation => allocation.ItemType)
					.ToArray();
				SearchState reserved = state;
				var allocationReservations = new List<VirtualResourceReservation>();
				foreach (ConcreteItemAllocation allocation in concrete) {
					VirtualReservationAttempt attempt = reserved.Ledger.TryReserveExact(allocation.ItemType, allocation.Quantity);
					if (!attempt.Succeeded)
						return One(context.Fail(state, RecursivePlanStatus.NoViableResourceAllocation, PlanningFrontierKind.NoViableResourceAllocation,
							consumingRecipe, requirement, requirement.Source, required, attempt.ShortageQuantity, depth, path));
					VirtualResourceReservation reservation = attempt.Reservation ?? throw new InvalidOperationException("Successful group reservation omitted evidence.");
					allocationReservations.Add(reservation);
					reserved = reserved.WithReservation(attempt.Ledger, reservation);
				}
				var allocationEvidence = new RecipeGroupAllocation(decisionId, requirement, required, accepted, concrete, allocationReservations);
				var resourceTrace = new ResourceTrace(decisionId, requirement, required, accepted, allocationEvidence);
				long availableBefore = AggregateAvailable(input.Ledger, accepted);
				return One(Outcome.Success(
					reserved.WithGroupAllocation(allocationEvidence, resourceTrace),
					new PlannedIngredientUse(requirement, required, availableBefore, Math.Max(0, required - availableBefore),
						AggregateAvailable(state.Ledger, accepted), AggregateAvailable(reserved.Ledger, accepted), allocationEvidence)));
			}
		}
		return DeduplicateOutcomes(results);
	}

	private static IReadOnlyList<Outcome> ProduceGroupMemberAlternatives(
		SearchContext context,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long required,
		ItemNode member,
		long productionNeeded,
		SearchState state,
		int depth,
		IReadOnlyList<PlanningPathEntry> path,
		IReadOnlyList<RecipeNode> producers)
	{
		// A concrete group member with multiple producers is still a recipe OR
		// decision. Reuse the same stable identity scheme with the group edge.
		AlternativeDecisionId id = new($"{CreateDecisionId(consumingRecipe, requirement, path).Value}/group-member:{member.ItemType}");
		IEnumerable<RecipeNode> selected = producers;
		bool explicitSelection = false;
		if (context.Request.ExplicitChoices.TryGetValue(id, out ExplicitRecipeChoice? explicitChoice)) {
			RecipeNode? exact = producers.FirstOrDefault(recipe => ReferenceEquals(recipe, explicitChoice.SelectedRecipe));
			if (exact is null) {
				AlternativeTrace invalid = new(id, consumingRecipe, requirement, required, 0, productionNeeded, depth, path, producers, explicitChoice.SelectedRecipe, true, true);
				return One(Outcome.Failure(state.WithAlternative(invalid), RecursivePlanStatus.InvalidExplicitChoice,
					context.Frontier(PlanningFrontierKind.InvalidExplicitChoice, consumingRecipe, requirement, member, required, 0, productionNeeded, depth, path, producers, null)));
			}
			selected = new[] { exact };
			explicitSelection = true;
		}
		var results = new List<Outcome>();
		foreach (RecipeNode producer in selected) {
			if (!explicitSelection && !context.TryExpand(out PlanningSearchStatistics _)) {
				results.Add(Outcome.Failure(state, RecursivePlanStatus.SearchBudgetBoundary,
					context.Frontier(PlanningFrontierKind.SearchBudgetBoundary, consumingRecipe, requirement, member, required, 0, productionNeeded, depth, path, producers, null)));
				break;
			}
			AlternativeTrace trace = new(id, consumingRecipe, requirement, required, 0, productionNeeded, depth, path, producers, producer, explicitSelection, false);
			results.AddRange(PlanRecipe(context, producer, productionNeeded, state.WithAlternative(trace), depth + 1, requirement.Ordinal, path));
		}
		return results;
	}

	private static RecursiveRecipePlanResult Aggregate(SearchContext context, IReadOnlyList<Outcome> rawOutcomes)
	{
		Outcome[] outcomes = DeduplicateOutcomes(rawOutcomes).ToArray();
		Outcome[] successes = outcomes.Where(outcome => outcome.Succeeded).OrderBy(outcome => outcome.DeterministicSignature, StringComparer.Ordinal).ToArray();
		HashSet<AlternativeDecisionId> encountered = outcomes.SelectMany(outcome => outcome.State.Alternatives).Select(trace => trace.Id).ToHashSet();
		if (context.Request.ExplicitChoices.Keys.Any(id => !encountered.Contains(id))) {
			Outcome representative = outcomes.FirstOrDefault() ?? Outcome.Failure(SearchState.Create(context.Request.Ledger), RecursivePlanStatus.InvalidExplicitChoice, null);
			return FailureResult(context, representative.WithStatus(RecursivePlanStatus.InvalidExplicitChoice));
		}

		IReadOnlyList<AlternativeDecision> decisions = BuildAlternativeDecisions(outcomes, successes);
		AlternativeDecision? pending = decisions.FirstOrDefault(decision => decision.State == AlternativeDecisionState.UserChoiceRequired);
		if (successes.Length > 0 && pending is not null) {
			return new RecursiveRecipePlanResult(
				context.Request.SelectedRootRecipe,
				context.Request.RequiredOutputQuantity,
				RecursivePlanStatus.UserChoiceRequired,
				context.Request.Ledger,
				context.Request.Ledger,
				Array.Empty<PlannedCraftStep>(),
				Array.Empty<VirtualResourceReservation>(),
				Array.Empty<VirtualResourceProduction>(),
				pending.Candidates.Select(candidate => candidate.Frontier).FirstOrDefault(frontier => frontier is not null),
				decisions.TakeWhile(decision => decision.DecisionId != pending.DecisionId).Append(pending).ToArray(),
				Array.Empty<RecipeChoiceApplication>(),
				Array.Empty<RecipeGroupAllocation>(),
				BuildResourceDecisions(outcomes, null),
				context.Statistics(successes.Length, outcomes.Length - successes.Length));
		}

		if (successes.Length > 0) {
			Outcome chosen = successes[0];
			IReadOnlyList<AlternativeDecision> chosenDecisions = decisions
				.Where(decision => chosen.State.Alternatives.Any(trace => trace.Id == decision.DecisionId))
				.ToArray();
			RecipeChoiceApplication[] applications = chosenDecisions
				.Where(decision => decision.IsResolved && decision.SelectedRecipe is not null)
				.Select(decision => new RecipeChoiceApplication(
					decision.DecisionId,
					decision.SelectedRecipe!,
					chosen.State.Alternatives.First(trace => trace.Id == decision.DecisionId).Explicit
						? RecipeChoiceApplicationKind.ExplicitUserChoice
						: RecipeChoiceApplicationKind.AutoSelectedOnlyViable))
				.ToArray();
			return new RecursiveRecipePlanResult(
				context.Request.SelectedRootRecipe,
				context.Request.RequiredOutputQuantity,
				RecursivePlanStatus.Success,
				context.Request.Ledger,
				chosen.State.Ledger,
				chosen.State.Steps.Select((step, index) => step.WithSequence(index)).ToArray(),
				chosen.State.Reservations,
				chosen.State.Productions,
				null,
				chosenDecisions,
				applications,
				chosen.State.GroupAllocations,
				BuildResourceDecisions(outcomes, chosen),
				context.Statistics(successes.Length, outcomes.Length - successes.Length));
		}

		Outcome failure = SelectRepresentativeFailure(outcomes);
		RecursivePlanStatus status;
		if (outcomes.Any(outcome => outcome.Status == RecursivePlanStatus.InvalidExplicitChoice))
			status = RecursivePlanStatus.InvalidExplicitChoice;
		else if (context.Request.ExplicitChoices.Count > 0 && encountered.Count > 0)
			status = RecursivePlanStatus.ExplicitChoiceFailed;
		else if (outcomes.Any(outcome => outcome.Status == RecursivePlanStatus.SearchBudgetBoundary))
			status = RecursivePlanStatus.SearchBudgetBoundary;
		else if (decisions.Count > 0 && outcomes.Any(outcome => IsDeferred(outcome.Status)))
			status = RecursivePlanStatus.AlternativeResolutionDeferred;
		else if (decisions.Count > 0)
			status = RecursivePlanStatus.NoViableAlternative;
		else
			status = failure.Status;
		RecipeChoiceApplication[] failedApplications = failure.State.Alternatives
			.Where(trace => trace.Explicit && !trace.InvalidExplicit)
			.Select(trace => new RecipeChoiceApplication(trace.Id, trace.Selected, RecipeChoiceApplicationKind.ExplicitUserChoice))
			.ToArray();
		return FailureResult(context, failure.WithStatus(status), decisions, BuildResourceDecisions(outcomes, null), failedApplications);
	}

	private static IReadOnlyList<AlternativeDecision> BuildAlternativeDecisions(IReadOnlyList<Outcome> outcomes, IReadOnlyList<Outcome> successes)
	{
		var ids = outcomes
			.SelectMany(outcome => outcome.State.Alternatives.Select((trace, index) => (trace.Id, Index: index)))
			.GroupBy(entry => entry.Id)
			.OrderBy(group => group.Min(entry => entry.Index))
			.ThenBy(group => group.Key.Value, StringComparer.Ordinal)
			.Select(group => group.Key)
			.ToArray();
		var later = new List<AlternativeDecision>();
		for (int idIndex = ids.Length - 1; idIndex >= 0; idIndex--) {
			AlternativeDecisionId id = ids[idIndex];
			AlternativeTrace exemplar = outcomes.SelectMany(outcome => outcome.State.Alternatives).First(trace => trace.Id == id);
			var candidates = new List<AlternativeCandidateResult>();
			foreach (RecipeNode recipe in exemplar.Candidates.OrderBy(recipe => recipe.RecipeIndex)) {
				Outcome[] selectedOutcomes = outcomes.Where(outcome => outcome.State.Alternatives.Any(trace => trace.Id == id && ReferenceEquals(trace.Selected, recipe))).ToArray();
				Outcome? successful = successes.FirstOrDefault(outcome => outcome.State.Alternatives.Any(trace => trace.Id == id && ReferenceEquals(trace.Selected, recipe)));
				Outcome? representative = successful ?? selectedOutcomes.FirstOrDefault();
				bool notApplicable = exemplar.Explicit && !ReferenceEquals(exemplar.Selected, recipe);
				AlternativeCandidateStatus candidateStatus = notApplicable
					? AlternativeCandidateStatus.NotApplicable
					: successful is not null ? AlternativeCandidateStatus.FullyViable : ClassifyCandidate(representative?.Status ?? RecursivePlanStatus.NoViablePlan);
				RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, exemplar.Deficit);
				candidates.Add(new AlternativeCandidateResult(
					recipe,
					candidateStatus,
					exemplar.Deficit,
					quantities.CraftRepetitions,
					quantities.ProducedQuantity,
					representative?.Status ?? RecursivePlanStatus.NoViablePlan,
					successful?.State.Ledger,
					successful?.State.Steps.Select((step, index) => step.WithSequence(index)).ToArray() ?? Array.Empty<PlannedCraftStep>(),
					successful?.State.Reservations ?? Array.Empty<VirtualResourceReservation>(),
					successful?.State.Productions ?? Array.Empty<VirtualResourceProduction>(),
					successful?.State.GroupAllocations ?? Array.Empty<RecipeGroupAllocation>(),
					Array.Empty<ResourceAllocationDecision>(),
					representative?.Frontier,
					later,
					later.FirstOrDefault(decision => !decision.IsResolved),
					Array.Empty<RecipeChoiceApplication>()));
			}
			int viableCount = candidates.Count(candidate => candidate.Status == AlternativeCandidateStatus.FullyViable);
			AlternativeDecisionState state;
			RecipeNode? selected;
			if (exemplar.InvalidExplicit) {
				state = AlternativeDecisionState.InvalidExplicitChoice;
				selected = exemplar.Selected;
			}
			else if (exemplar.Explicit) {
				state = viableCount == 1 ? AlternativeDecisionState.ExplicitlySelected : AlternativeDecisionState.ExplicitChoiceFailed;
				selected = exemplar.Selected;
			}
			else if (viableCount >= 2) {
				state = AlternativeDecisionState.UserChoiceRequired;
				selected = null;
			}
			else if (viableCount == 1) {
				state = AlternativeDecisionState.AutoSelectedOnlyViable;
				selected = candidates.Single(candidate => candidate.Status == AlternativeCandidateStatus.FullyViable).Recipe;
			}
			else if (candidates.Any(candidate => candidate.Status is AlternativeCandidateStatus.Deferred or AlternativeCandidateStatus.RequiresUserChoice)) {
				state = candidates.Any(candidate => candidate.Status == AlternativeCandidateStatus.RequiresUserChoice)
					? AlternativeDecisionState.UserChoiceRequired
					: AlternativeDecisionState.UnresolvedDeferred;
				selected = null;
			}
			else {
				state = AlternativeDecisionState.NoViableCandidate;
				selected = null;
			}
			var decision = new AlternativeDecision(id, state, exemplar.ConsumingRecipe, exemplar.Requirement, exemplar.Required,
				exemplar.Available, exemplar.Deficit, exemplar.Depth, exemplar.Path, candidates, selected);
			later.Insert(0, decision);
		}
		return later;
	}

	private static IReadOnlyList<ResourceAllocationDecision> BuildResourceDecisions(IReadOnlyList<Outcome> outcomes, Outcome? selectedOutcome)
	{
		return outcomes.SelectMany(outcome => outcome.State.Resources)
			.GroupBy(trace => trace.Id)
			.OrderBy(group => group.Key.Value, StringComparer.Ordinal)
			.Select(group => {
				ResourceTrace exemplar = group.First();
				RecipeGroupAllocation[] allocations = group.Select(trace => trace.Allocation)
					.GroupBy(allocation => allocation.DeterministicSignature, StringComparer.Ordinal)
					.Select(values => values.First())
					.OrderBy(allocation => allocation.DeterministicSignature, StringComparer.Ordinal)
					.ToArray();
				var candidates = allocations.Select(allocation => {
					Outcome? outcome = outcomes.FirstOrDefault(candidate => candidate.State.Resources.Any(trace => trace.Id == exemplar.Id && trace.Allocation.DeterministicSignature == allocation.DeterministicSignature));
					bool rootSuccess = outcomes.Any(candidate => candidate.Succeeded && candidate.State.Resources.Any(trace => trace.Id == exemplar.Id && trace.Allocation.DeterministicSignature == allocation.DeterministicSignature));
					ResourceAllocationCandidateStatus status = rootSuccess ? ResourceAllocationCandidateStatus.RootSuccessful
						: outcome?.Status == RecursivePlanStatus.SearchBudgetBoundary ? ResourceAllocationCandidateStatus.SearchBudgetBoundary
						: IsDeferred(outcome?.Status ?? RecursivePlanStatus.NoViablePlan) ? ResourceAllocationCandidateStatus.Deferred
						: ResourceAllocationCandidateStatus.Failed;
					return new ResourceAllocationCandidateResult(allocation, status, outcome?.Status ?? RecursivePlanStatus.NoViablePlan, outcome?.Frontier);
				}).ToArray();
				RecipeGroupAllocation? selected = selectedOutcome?.State.Resources
					.FirstOrDefault(trace => trace.Id == exemplar.Id)?.Allocation;
				return new ResourceAllocationDecision(exemplar.Id, exemplar.Requirement, exemplar.Required, exemplar.Accepted, candidates, selected);
			})
			.ToArray();
	}

	private static RecursiveRecipePlanResult FailureResult(
		SearchContext context,
		Outcome failure,
		IReadOnlyList<AlternativeDecision>? decisions = null,
		IReadOnlyList<ResourceAllocationDecision>? resourceDecisions = null,
		IReadOnlyList<RecipeChoiceApplication>? appliedChoices = null) =>
		new(
			context.Request.SelectedRootRecipe,
			context.Request.RequiredOutputQuantity,
			failure.Status,
			context.Request.Ledger,
			context.Request.Ledger,
			Array.Empty<PlannedCraftStep>(),
			Array.Empty<VirtualResourceReservation>(),
			Array.Empty<VirtualResourceProduction>(),
			failure.Frontier,
			decisions ?? Array.Empty<AlternativeDecision>(),
			appliedChoices ?? Array.Empty<RecipeChoiceApplication>(),
			Array.Empty<RecipeGroupAllocation>(),
			resourceDecisions ?? Array.Empty<ResourceAllocationDecision>(),
			context.Statistics(0, 1));

	private static Outcome SelectRepresentativeFailure(IReadOnlyList<Outcome> outcomes) =>
		outcomes.OrderBy(outcome => FailureRank(outcome.Status)).ThenBy(outcome => outcome.DeterministicSignature, StringComparer.Ordinal).First();

	private static int FailureRank(RecursivePlanStatus status) => status switch
	{
		RecursivePlanStatus.InvalidExplicitChoice => 0,
		RecursivePlanStatus.SearchBudgetBoundary => 1,
		RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.TargetSubgraphTruncated or RecursivePlanStatus.CycleBoundary or RecursivePlanStatus.PlanningDepthBoundary => 2,
		RecursivePlanStatus.BlockedByStation or RecursivePlanStatus.BlockedByCondition => 3,
		_ => 4
	};

	private static AlternativeCandidateStatus ClassifyCandidate(RecursivePlanStatus status) => status switch
	{
		RecursivePlanStatus.UserChoiceRequired => AlternativeCandidateStatus.RequiresUserChoice,
		RecursivePlanStatus.BlockedByStation or RecursivePlanStatus.BlockedByCondition => AlternativeCandidateStatus.Blocked,
		RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.TargetSubgraphTruncated or RecursivePlanStatus.CycleBoundary
			or RecursivePlanStatus.PlanningDepthBoundary or RecursivePlanStatus.AlternativeResolutionDeferred
			or RecursivePlanStatus.SearchBudgetBoundary => AlternativeCandidateStatus.Deferred,
		RecursivePlanStatus.InvalidExplicitChoice => AlternativeCandidateStatus.NotApplicable,
		_ => AlternativeCandidateStatus.Failed
	};

	private static bool IsDeferred(RecursivePlanStatus status) => status is
		RecursivePlanStatus.UnknownConstraint or RecursivePlanStatus.TargetSubgraphTruncated or RecursivePlanStatus.CycleBoundary
		or RecursivePlanStatus.PlanningDepthBoundary or RecursivePlanStatus.AlternativeResolutionDeferred
		or RecursivePlanStatus.SearchBudgetBoundary;

	private static AlternativeDecisionId CreateDecisionId(RecipeNode consumingRecipe, IngredientEdge requirement, IReadOnlyList<PlanningPathEntry> path)
	{
		string pathText = string.Join("/", path.Select(entry => $"{entry.Recipe.RecipeIndex}@{entry.IncomingIngredientOrdinal?.ToString() ?? "root"}"));
		return new AlternativeDecisionId($"recipe-choice:{pathText}/ingredient:{consumingRecipe.RecipeIndex}:{requirement.Ordinal}:item:{requirement.Source.ItemType}");
	}

	private static ResourceAllocationDecisionId CreateResourceDecisionId(RecipeNode consumingRecipe, IngredientEdge requirement, IReadOnlyList<PlanningPathEntry> path)
	{
		string pathText = string.Join("/", path.Select(entry => $"{entry.Recipe.RecipeIndex}@{entry.IncomingIngredientOrdinal?.ToString() ?? "root"}"));
		return new ResourceAllocationDecisionId($"resource-allocation:{pathText}/ingredient:{consumingRecipe.RecipeIndex}:{requirement.Ordinal}");
	}

	private static long AggregateAvailable(VirtualResourceLedger ledger, IEnumerable<int> itemTypes) =>
		itemTypes.Distinct().Sum(itemType => Math.Max(0, ledger.GetBalance(itemType).RemainingQuantity));

	private static List<ConcreteItemAllocation> Append(IReadOnlyList<ConcreteItemAllocation> source, int itemType, long quantity)
	{
		var result = new List<ConcreteItemAllocation>(source);
		if (quantity > 0)
			result.Add(new ConcreteItemAllocation(itemType, quantity));
		return result;
	}

	private static IReadOnlyList<Outcome> One(Outcome outcome) => new[] { outcome };

	private static IReadOnlyList<Outcome> DeduplicateOutcomes(IEnumerable<Outcome> outcomes) => outcomes
		.GroupBy(outcome => $"{outcome.Succeeded}:{outcome.Status}:{outcome.DeterministicSignature}", StringComparer.Ordinal)
		.Select(group => group.First())
		.OrderBy(outcome => outcome.DeterministicSignature, StringComparer.Ordinal)
		.ToArray();

	private sealed class SearchContext
	{
		private int _expandedBranches;
		private bool _budgetExhausted;

		public SearchContext(RecursiveRecipePlanningRequest request)
		{
			Request = request;
			IncludedRecipes = new HashSet<RecipeNode>(request.TargetSubgraph.RecipeNodes, ReferenceEqualityComparer.Instance);
			IncludedItems = request.TargetSubgraph.ItemNodes.ToDictionary(item => item.ItemType);
		}

		public RecursiveRecipePlanningRequest Request { get; }
		public HashSet<RecipeNode> IncludedRecipes { get; }
		public IReadOnlyDictionary<int, ItemNode> IncludedItems { get; }

		public bool TryExpand(out PlanningSearchStatistics statistics)
		{
			if (_expandedBranches >= Request.Limits.MaximumBranchExpansions) {
				_budgetExhausted = true;
				statistics = Statistics(0, 0);
				return false;
			}
			_expandedBranches++;
			statistics = Statistics(0, 0);
			return true;
		}

		public PlanningSearchStatistics Statistics(int successes, int failures) =>
			new(_expandedBranches, Request.Limits.MaximumBranchExpansions, _budgetExhausted, successes, failures);

		public (RecursivePlanStatus Status, PlanningFrontierKind Kind) ZeroProducerBoundary(ItemNode item)
		{
			if (item.Classification == ItemClassification.Atomic)
				return (RecursivePlanStatus.InsufficientTerminalResource, PlanningFrontierKind.InsufficientTerminalResource);
			if (Request.TargetSubgraph.IsTruncated)
				return (RecursivePlanStatus.TargetSubgraphTruncated, PlanningFrontierKind.TargetSubgraphTruncated);
			return (RecursivePlanStatus.NoProducerInTargetSubgraph, PlanningFrontierKind.NoProducerInTargetSubgraph);
		}

		public Outcome Fail(SearchState state, RecursivePlanStatus status, PlanningFrontierKind kind, RecipeNode recipe,
			IngredientEdge? requirement, ItemNode item, long required, long deficit, int depth,
			IReadOnlyList<PlanningPathEntry> path, IReadOnlyList<RecipeNode>? candidates = null, RecipeResolution? resolutionEvidence = null)
		{
			long available = Math.Max(0, required - deficit);
			return Outcome.Failure(state, status, Frontier(kind, recipe, requirement, item, required, available, deficit, depth, path, candidates ?? Array.Empty<RecipeNode>(), resolutionEvidence));
		}

		public PlanningFrontier Frontier(PlanningFrontierKind kind, RecipeNode recipe, IngredientEdge? requirement, ItemNode item,
			long required, long available, long deficit, int depth, IReadOnlyList<PlanningPathEntry> path,
			IReadOnlyList<RecipeNode> candidates, RecipeResolution? resolutionEvidence) =>
			new(kind, recipe, requirement, item, required, available, deficit, depth, path, candidates, resolutionEvidence, Request.TargetSubgraph);
	}

	private sealed class SearchState
	{
		private SearchState(VirtualResourceLedger ledger, IReadOnlyList<PlannedCraftStep> steps,
			IReadOnlyList<VirtualResourceReservation> reservations, IReadOnlyList<VirtualResourceProduction> productions,
			IReadOnlyList<RecipeGroupAllocation> groupAllocations, IReadOnlyList<AlternativeTrace> alternatives,
			IReadOnlyList<ResourceTrace> resources)
		{
			Ledger = ledger; Steps = steps; Reservations = reservations; Productions = productions;
			GroupAllocations = groupAllocations; Alternatives = alternatives; Resources = resources;
		}

		public VirtualResourceLedger Ledger { get; }
		public IReadOnlyList<PlannedCraftStep> Steps { get; }
		public IReadOnlyList<VirtualResourceReservation> Reservations { get; }
		public IReadOnlyList<VirtualResourceProduction> Productions { get; }
		public IReadOnlyList<RecipeGroupAllocation> GroupAllocations { get; }
		public IReadOnlyList<AlternativeTrace> Alternatives { get; }
		public IReadOnlyList<ResourceTrace> Resources { get; }

		public static SearchState Create(VirtualResourceLedger ledger) => new(ledger, Array.Empty<PlannedCraftStep>(), Array.Empty<VirtualResourceReservation>(),
			Array.Empty<VirtualResourceProduction>(), Array.Empty<RecipeGroupAllocation>(), Array.Empty<AlternativeTrace>(), Array.Empty<ResourceTrace>());
		public SearchState WithReservation(VirtualResourceLedger ledger, VirtualResourceReservation reservation) => new(ledger, Steps, Reservations.Append(reservation).ToArray(), Productions, GroupAllocations, Alternatives, Resources);
		public SearchState WithProduction(VirtualProductionCredit credit) => new(credit.Ledger, Steps, Reservations, Productions.Append(credit.Production).ToArray(), GroupAllocations, Alternatives, Resources);
		public SearchState WithStep(PlannedCraftStep step) => new(Ledger, Steps.Append(step).ToArray(), Reservations, Productions, GroupAllocations, Alternatives, Resources);
		public SearchState WithAlternative(AlternativeTrace trace) => new(Ledger, Steps, Reservations, Productions, GroupAllocations, Alternatives.Append(trace).ToArray(), Resources);
		public SearchState WithGroupAllocation(RecipeGroupAllocation allocation, ResourceTrace trace) => new(Ledger, Steps, Reservations, Productions, GroupAllocations.Append(allocation).ToArray(), Alternatives, Resources.Append(trace).ToArray());
	}

	private sealed class Outcome
	{
		private Outcome(SearchState state, bool succeeded, RecursivePlanStatus status, PlanningFrontier? frontier, PlannedIngredientUse? ingredientUse) { State = state; Succeeded = succeeded; Status = status; Frontier = frontier; IngredientUse = ingredientUse; }
		public SearchState State { get; }
		public bool Succeeded { get; }
		public RecursivePlanStatus Status { get; }
		public PlanningFrontier? Frontier { get; }
		public PlannedIngredientUse? IngredientUse { get; }
		public string DeterministicSignature => string.Join("/", State.Alternatives.Select(trace => trace.Selected.RecipeIndex)) + "|" +
			string.Join("/", State.Resources.Select(trace => trace.Allocation.DeterministicSignature)) + "|" +
			string.Join("/", State.Steps.Select(step => step.Recipe.RecipeIndex));
		public static Outcome Success(SearchState state, PlannedIngredientUse? ingredientUse = null) => new(state, true, RecursivePlanStatus.Success, null, ingredientUse);
		public static Outcome Failure(SearchState state, RecursivePlanStatus status, PlanningFrontier? frontier) => new(state, false, status, frontier, null);
		public Outcome WithStatus(RecursivePlanStatus status) => new(State, false, status, Frontier, null);
	}

	private sealed record AlternativeTrace(AlternativeDecisionId Id, RecipeNode ConsumingRecipe, IngredientEdge Requirement,
		long Required, long Available, long Deficit, int Depth, IReadOnlyList<PlanningPathEntry> Path,
		IReadOnlyList<RecipeNode> Candidates, RecipeNode Selected, bool Explicit, bool InvalidExplicit);
	private sealed record ResourceTrace(ResourceAllocationDecisionId Id, IngredientEdge Requirement, long Required,
		IReadOnlyList<int> Accepted, RecipeGroupAllocation Allocation);
}
