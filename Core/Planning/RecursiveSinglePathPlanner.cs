using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Core.Planning;

public static class RecursiveSinglePathPlanner
{
	public static RecursiveRecipePlanResult Plan(RecursiveRecipePlanningRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		var context = new PlanningContext(request);
		if (!context.IncludedRecipes.Contains(request.SelectedRootRecipe)) {
			PlanningFrontier frontier = context.CreateFrontier(
				PlanningFrontierKind.RecipeOutsideTargetSubgraph,
				request.SelectedRootRecipe,
				requirement: null,
				request.SelectedRootRecipe.Result,
				request.RequiredOutputQuantity,
				request.Ledger.GetBalance(request.SelectedRootRecipe.Result.ItemType).RemainingQuantity,
				request.RequiredOutputQuantity,
				depth: 0,
				Array.Empty<PlanningPathEntry>(),
				Array.Empty<RecipeNode>(),
				resolutionEvidence: null);
			return FailureResult(request, Attempt.Failure(RecursivePlanStatus.RecipeOutsideTargetSubgraph, request.Ledger, frontier));
		}

		Attempt attempt = PlanRecipe(
			context,
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			request.Ledger,
			depth: 0,
			incomingIngredientOrdinal: null,
			Array.Empty<PlanningPathEntry>());
		if (!attempt.Succeeded)
			return FailureResult(request, attempt);

		PlannedCraftStep[] orderedSteps = attempt.Steps
			.Select((step, sequence) => step.WithSequence(sequence))
			.ToArray();
		return new RecursiveRecipePlanResult(
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			RecursivePlanStatus.Success,
			request.Ledger,
			attempt.Ledger,
			orderedSteps,
			attempt.Reservations,
			attempt.Productions,
			frontier: null,
			attempt.Decisions,
			attempt.AppliedChoices);
	}

	private static Attempt PlanRecipe(
		PlanningContext context,
		RecipeNode recipe,
		long requestedOutputQuantity,
		VirtualResourceLedger inputLedger,
		int depth,
		int? incomingIngredientOrdinal,
		IReadOnlyList<PlanningPathEntry> dependencyPath)
	{
		var currentEntry = new PlanningPathEntry(depth, recipe, requestedOutputQuantity, incomingIngredientOrdinal);
		PlanningPathEntry[] currentPath = dependencyPath.Append(currentEntry).ToArray();
		if (depth > context.Request.Limits.MaximumDependencyDepth) {
			return context.Failure(
				RecursivePlanStatus.PlanningDepthBoundary,
				PlanningFrontierKind.PlanningDepthBoundary,
				recipe,
				requirement: null,
				recipe.Result,
				requestedOutputQuantity,
				inputLedger.GetBalance(recipe.Result.ItemType).RemainingQuantity,
				requestedOutputQuantity,
				depth,
				currentPath,
				Array.Empty<RecipeNode>(),
				resolutionEvidence: null,
				inputLedger);
		}

		if (dependencyPath.Any(entry => ReferenceEquals(entry.Recipe, recipe))) {
			return context.Failure(
				RecursivePlanStatus.CycleBoundary,
				PlanningFrontierKind.CycleBoundary,
				recipe,
				requirement: null,
				recipe.Result,
				requestedOutputQuantity,
				inputLedger.GetBalance(recipe.Result.ItemType).RemainingQuantity,
				requestedOutputQuantity,
				depth,
				currentPath,
				new[] { recipe },
				resolutionEvidence: null,
				inputLedger);
		}

		RecipeResolution resolution = context.Request.Resolution.GetRecipeResolution(recipe);
		(RecursivePlanStatus Status, PlanningFrontierKind Kind)? externalBlock = resolution.State switch
		{
			RecipeLocalState.BlockedByStation => (RecursivePlanStatus.BlockedByStation, PlanningFrontierKind.BlockedByStation),
			RecipeLocalState.BlockedByCondition => (RecursivePlanStatus.BlockedByCondition, PlanningFrontierKind.BlockedByCondition),
			RecipeLocalState.UnknownConstraint => (RecursivePlanStatus.UnknownConstraint, PlanningFrontierKind.UnknownConstraint),
			RecipeLocalState.ReadyNow or RecipeLocalState.NeedsIngredients => null,
			_ => throw new InvalidOperationException($"Unsupported recipe local state {resolution.State}.")
		};
		if (externalBlock is { } block) {
			return context.Failure(
				block.Status,
				block.Kind,
				recipe,
				requirement: null,
				recipe.Result,
				requestedOutputQuantity,
				inputLedger.GetBalance(recipe.Result.ItemType).RemainingQuantity,
				requestedOutputQuantity,
				depth,
				currentPath,
				Array.Empty<RecipeNode>(),
				resolution,
				inputLedger);
		}

		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutputQuantity);
		IngredientEdge[] ingredients = recipe.Ingredients
			.OrderBy(ingredient => ingredient.Ordinal)
			.ThenBy(ingredient => ingredient.Source.ItemType)
			.ToArray();
		IngredientEdge? groupRequirement = ingredients.FirstOrDefault(ingredient => ingredient.AcceptsRecipeGroup);
		if (groupRequirement is not null) {
			long required = quantities.ScaleIngredient(groupRequirement);
			long available = inputLedger.GetBalance(groupRequirement.Source.ItemType).RemainingQuantity;
			return context.Failure(
				RecursivePlanStatus.DeferredRecipeGroupAllocation,
				PlanningFrontierKind.DeferredRecipeGroupAllocation,
				recipe,
				groupRequirement,
				groupRequirement.Source,
				required,
				available,
				Math.Max(0, required - available),
				depth,
				currentPath,
				Array.Empty<RecipeNode>(),
				resolution,
				inputLedger);
		}

		VirtualResourceLedger trialLedger = inputLedger;
		var steps = new List<PlannedCraftStep>();
		var reservations = new List<VirtualResourceReservation>();
		var productions = new List<VirtualResourceProduction>();
		var decisions = new List<AlternativeDecision>();
		var appliedChoices = new List<RecipeChoiceApplication>();
		var ingredientUses = new List<PlannedIngredientUse>(ingredients.Length);
		foreach (IngredientEdge ingredient in ingredients) {
			long required = quantities.ScaleIngredient(ingredient);
			long availableBeforeExpansion = trialLedger.GetBalance(ingredient.Source.ItemType).RemainingQuantity;
			long deficit = Math.Max(0, required - availableBeforeExpansion);
			if (deficit > 0) {
				RecipeNode[] producers = ingredient.Source.ProducerRecipes
					.Where(context.IncludedRecipes.Contains)
					.OrderBy(producer => producer.RecipeIndex)
					.ToArray();
				if (producers.Length == 0) {
					(RecursivePlanStatus frontierStatus, PlanningFrontierKind frontierKind) = context.ZeroProducerBoundary(ingredient.Source);
					return context.Failure(
						frontierStatus,
						frontierKind,
						recipe,
						ingredient,
						ingredient.Source,
						required,
						availableBeforeExpansion,
						deficit,
						depth,
						currentPath,
						Array.Empty<RecipeNode>(),
						resolutionEvidence: null,
						trialLedger)
						.WithPrependedProvenance(decisions, appliedChoices);
				}

				Attempt child = producers.Length == 1
					? PlanRecipe(context, producers[0], deficit, trialLedger, depth + 1, ingredient.Ordinal, currentPath)
					: EvaluateAlternative(
						context,
						recipe,
						ingredient,
						required,
						availableBeforeExpansion,
						deficit,
						trialLedger,
						depth,
						currentPath,
						producers);
				if (!child.Succeeded)
					return child.WithPrependedProvenance(decisions, appliedChoices);

				trialLedger = child.Ledger;
				steps.AddRange(child.Steps);
				reservations.AddRange(child.Reservations);
				productions.AddRange(child.Productions);
				decisions.AddRange(child.Decisions);
				appliedChoices.AddRange(child.AppliedChoices);
			}

			long availableBeforeReservation = trialLedger.GetBalance(ingredient.Source.ItemType).RemainingQuantity;
			VirtualReservationAttempt reservationAttempt = trialLedger.TryReserveExact(ingredient.Source.ItemType, required);
			if (!reservationAttempt.Succeeded)
				throw new InvalidOperationException("Recursive production succeeded without making the parent requirement reservable.");

			VirtualResourceReservation reservation = reservationAttempt.Reservation
				?? throw new InvalidOperationException("A successful reservation did not return evidence.");
			trialLedger = reservationAttempt.Ledger;
			reservations.Add(reservation);
			ingredientUses.Add(new PlannedIngredientUse(
				ingredient,
				required,
				availableBeforeExpansion,
				deficit,
				availableBeforeReservation,
				reservation.RemainingAfter));
		}

		VirtualProductionCredit credit = trialLedger.CreditPlannedProduction(recipe.Result.ItemType, quantities.ProducedQuantity);
		trialLedger = credit.Ledger;
		productions.Add(credit.Production);
		steps.Add(new PlannedCraftStep(
			sequence: steps.Count,
			dependencyDepth: depth,
			recipe,
			requestedOutputQuantity,
			quantities.CraftRepetitions,
			quantities.ProducedQuantity,
			ingredientUses));
		return Attempt.Success(trialLedger, steps, reservations, productions, decisions, appliedChoices);
	}

	private static Attempt EvaluateAlternative(
		PlanningContext context,
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		long requiredQuantity,
		long availableQuantity,
		long deficitQuantity,
		VirtualResourceLedger decisionLedger,
		int depth,
		IReadOnlyList<PlanningPathEntry> dependencyPath,
		IReadOnlyList<RecipeNode> orderedCandidates)
	{
		AlternativeDecisionId decisionId = CreateDecisionId(consumingRecipe, requirement, dependencyPath);
		if (context.Request.ExplicitChoices.TryGetValue(decisionId, out ExplicitRecipeChoice? explicitChoice)) {
			RecipeNode? selected = orderedCandidates.FirstOrDefault(candidate => ReferenceEquals(candidate, explicitChoice.SelectedRecipe));
			if (selected is null) {
				AlternativeCandidateResult[] invalidCandidates = orderedCandidates
					.Select(candidate => NotApplicableCandidate(candidate, deficitQuantity))
					.ToArray();
				var invalidDecision = new AlternativeDecision(
					decisionId,
					AlternativeDecisionState.InvalidExplicitChoice,
					consumingRecipe,
					requirement,
					requiredQuantity,
					availableQuantity,
					deficitQuantity,
					depth,
					dependencyPath,
					invalidCandidates,
					explicitChoice.SelectedRecipe);
				PlanningFrontier frontier = context.CreateFrontier(
					PlanningFrontierKind.InvalidExplicitChoice,
					consumingRecipe,
					requirement,
					requirement.Source,
					requiredQuantity,
					availableQuantity,
					deficitQuantity,
					depth,
					dependencyPath,
					orderedCandidates,
					resolutionEvidence: null);
				return Attempt.Failure(RecursivePlanStatus.InvalidExplicitChoice, decisionLedger, frontier)
					.WithDecision(invalidDecision);
			}

			Attempt selectedAttempt = PlanRecipe(
				context,
				selected,
				deficitQuantity,
				decisionLedger,
				depth + 1,
				requirement.Ordinal,
				dependencyPath);
			AlternativeCandidateResult selectedResult = ToCandidateResult(selected, deficitQuantity, selectedAttempt);
			AlternativeCandidateResult[] explicitCandidates = orderedCandidates
				.Select(candidate => ReferenceEquals(candidate, selected)
					? selectedResult
					: NotApplicableCandidate(candidate, deficitQuantity))
				.ToArray();
			AlternativeDecisionState state = selectedAttempt.Succeeded
				|| selectedResult.Status is AlternativeCandidateStatus.RequiresUserChoice or AlternativeCandidateStatus.Deferred
					? AlternativeDecisionState.ExplicitlySelected
					: AlternativeDecisionState.ExplicitChoiceFailed;
			var decision = new AlternativeDecision(
				decisionId,
				state,
				consumingRecipe,
				requirement,
				requiredQuantity,
				availableQuantity,
				deficitQuantity,
				depth,
				dependencyPath,
				explicitCandidates,
				selected);
			var application = new RecipeChoiceApplication(decisionId, selected, RecipeChoiceApplicationKind.ExplicitUserChoice);
			if (selectedAttempt.Succeeded)
				return selectedAttempt.WithDecision(decision, application);

			RecursivePlanStatus selectedStatus = selectedResult.Status switch
			{
				AlternativeCandidateStatus.RequiresUserChoice => RecursivePlanStatus.UserChoiceRequired,
				AlternativeCandidateStatus.Deferred => RecursivePlanStatus.AlternativeResolutionDeferred,
				_ => RecursivePlanStatus.ExplicitChoiceFailed
			};
			return selectedAttempt.WithStatus(selectedStatus).WithDecision(decision, application);
		}

		var evaluated = new List<(RecipeNode Recipe, Attempt Attempt, AlternativeCandidateResult Result)>(orderedCandidates.Count);
		foreach (RecipeNode candidate in orderedCandidates) {
			Attempt candidateAttempt = PlanRecipe(
				context,
				candidate,
				deficitQuantity,
				decisionLedger,
				depth + 1,
				requirement.Ordinal,
				dependencyPath);
			evaluated.Add((candidate, candidateAttempt, ToCandidateResult(candidate, deficitQuantity, candidateAttempt)));
		}

		var candidateResults = evaluated.Select(entry => entry.Result).ToArray();
		var fullyViable = evaluated.Where(entry => entry.Result.Status == AlternativeCandidateStatus.FullyViable).ToArray();
		bool hasRequiresChoice = evaluated.Any(entry => entry.Result.Status == AlternativeCandidateStatus.RequiresUserChoice);
		bool hasDeferred = evaluated.Any(entry => entry.Result.Status == AlternativeCandidateStatus.Deferred);
		if (fullyViable.Length == 1 && !hasRequiresChoice && !hasDeferred) {
			var decision = new AlternativeDecision(
				decisionId,
				AlternativeDecisionState.AutoSelectedOnlyViable,
				consumingRecipe,
				requirement,
				requiredQuantity,
				availableQuantity,
				deficitQuantity,
				depth,
				dependencyPath,
				candidateResults,
				fullyViable[0].Recipe);
			var application = new RecipeChoiceApplication(decisionId, fullyViable[0].Recipe, RecipeChoiceApplicationKind.AutoSelectedOnlyViable);
			return fullyViable[0].Attempt.WithDecision(decision, application);
		}

		AlternativeDecisionState unresolvedState;
		RecursivePlanStatus unresolvedStatus;
		if (fullyViable.Length >= 2 || (fullyViable.Length == 0 && hasRequiresChoice)) {
			unresolvedState = AlternativeDecisionState.UserChoiceRequired;
			unresolvedStatus = RecursivePlanStatus.UserChoiceRequired;
		}
		else if (hasRequiresChoice || hasDeferred) {
			unresolvedState = AlternativeDecisionState.UnresolvedDeferred;
			unresolvedStatus = RecursivePlanStatus.AlternativeResolutionDeferred;
		}
		else {
			unresolvedState = AlternativeDecisionState.NoViableCandidate;
			unresolvedStatus = RecursivePlanStatus.NoViableAlternative;
		}

		var unresolvedDecision = new AlternativeDecision(
			decisionId,
			unresolvedState,
			consumingRecipe,
			requirement,
			requiredQuantity,
			availableQuantity,
			deficitQuantity,
			depth,
			dependencyPath,
			candidateResults,
			selectedRecipe: null);
		PlanningFrontier? representativeFrontier = evaluated
			.Select(entry => entry.Attempt.Frontier)
			.FirstOrDefault(frontier => frontier is not null);
		return Attempt.Failure(unresolvedStatus, decisionLedger, representativeFrontier)
			.WithDecision(unresolvedDecision);
	}

	private static AlternativeCandidateResult ToCandidateResult(
		RecipeNode recipe,
		long requestedOutputQuantity,
		Attempt attempt)
	{
		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutputQuantity);
		AlternativeCandidateStatus status = ClassifyCandidate(attempt);
		bool isPreview = status == AlternativeCandidateStatus.FullyViable;
		return new AlternativeCandidateResult(
			recipe,
			status,
			requestedOutputQuantity,
			quantities.CraftRepetitions,
			quantities.ProducedQuantity,
			attempt.Status,
			isPreview ? attempt.Ledger : null,
			isPreview ? attempt.Steps : Array.Empty<PlannedCraftStep>(),
			isPreview ? attempt.Reservations : Array.Empty<VirtualResourceReservation>(),
			isPreview ? attempt.Productions : Array.Empty<VirtualResourceProduction>(),
			attempt.Frontier,
			attempt.Decisions,
			attempt.PendingDecision,
			attempt.AppliedChoices);
	}

	private static AlternativeCandidateResult NotApplicableCandidate(RecipeNode recipe, long requestedOutputQuantity)
	{
		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutputQuantity);
		return new AlternativeCandidateResult(
			recipe,
			AlternativeCandidateStatus.NotApplicable,
			requestedOutputQuantity,
			quantities.CraftRepetitions,
			quantities.ProducedQuantity,
			RecursivePlanStatus.InvalidExplicitChoice,
			resultLedger: null,
			Array.Empty<PlannedCraftStep>(),
			Array.Empty<VirtualResourceReservation>(),
			Array.Empty<VirtualResourceProduction>(),
			frontier: null,
			Array.Empty<AlternativeDecision>(),
			nestedDecision: null,
			Array.Empty<RecipeChoiceApplication>());
	}

	private static AlternativeCandidateStatus ClassifyCandidate(Attempt attempt)
	{
		if (attempt.Succeeded)
			return AlternativeCandidateStatus.FullyViable;
		return attempt.Status switch
		{
			RecursivePlanStatus.UserChoiceRequired => AlternativeCandidateStatus.RequiresUserChoice,
			RecursivePlanStatus.BlockedByStation or RecursivePlanStatus.BlockedByCondition => AlternativeCandidateStatus.Blocked,
			RecursivePlanStatus.DeferredRecipeGroupAllocation
				or RecursivePlanStatus.TargetSubgraphTruncated
				or RecursivePlanStatus.UnknownConstraint
				or RecursivePlanStatus.CycleBoundary
				or RecursivePlanStatus.PlanningDepthBoundary
				or RecursivePlanStatus.AlternativeResolutionDeferred => AlternativeCandidateStatus.Deferred,
			RecursivePlanStatus.InvalidExplicitChoice => AlternativeCandidateStatus.NotApplicable,
			_ => AlternativeCandidateStatus.Failed
		};
	}

	private static AlternativeDecisionId CreateDecisionId(
		RecipeNode consumingRecipe,
		IngredientEdge requirement,
		IReadOnlyList<PlanningPathEntry> dependencyPath)
	{
		string path = string.Join("/", dependencyPath.Select(entry =>
			$"{entry.Recipe.RecipeIndex}@{(entry.IncomingIngredientOrdinal?.ToString() ?? "root")}"));
		return new AlternativeDecisionId(
			$"recipe-choice:{path}/ingredient:{consumingRecipe.RecipeIndex}:{requirement.Ordinal}:item:{requirement.Source.ItemType}");
	}

	private static RecursiveRecipePlanResult FailureResult(
		RecursiveRecipePlanningRequest request,
		Attempt attempt) =>
		new(
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			attempt.Status,
			request.Ledger,
			request.Ledger,
			Array.Empty<PlannedCraftStep>(),
			Array.Empty<VirtualResourceReservation>(),
			Array.Empty<VirtualResourceProduction>(),
			attempt.Frontier,
			attempt.Decisions,
			attempt.AppliedChoices);

	private sealed class PlanningContext
	{
		public PlanningContext(RecursiveRecipePlanningRequest request)
		{
			Request = request;
			IncludedRecipes = new HashSet<RecipeNode>(request.TargetSubgraph.RecipeNodes, ReferenceEqualityComparer.Instance);
		}

		public RecursiveRecipePlanningRequest Request { get; }

		public HashSet<RecipeNode> IncludedRecipes { get; }

		public (RecursivePlanStatus Status, PlanningFrontierKind Kind) ZeroProducerBoundary(ItemNode item)
		{
			if (item.Classification == ItemClassification.Atomic)
				return (RecursivePlanStatus.InsufficientTerminalResource, PlanningFrontierKind.InsufficientTerminalResource);
			if (Request.TargetSubgraph.IsTruncated)
				return (RecursivePlanStatus.TargetSubgraphTruncated, PlanningFrontierKind.TargetSubgraphTruncated);
			return (RecursivePlanStatus.NoProducerInTargetSubgraph, PlanningFrontierKind.NoProducerInTargetSubgraph);
		}

		public Attempt Failure(
			RecursivePlanStatus status,
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
			VirtualResourceLedger ledger) =>
			Attempt.Failure(status, ledger, CreateFrontier(
				kind,
				blockingRecipe,
				requirement,
				item,
				requiredQuantity,
				availableQuantity,
				deficitQuantity,
				depth,
				dependencyPath,
				candidateProducers,
				resolutionEvidence));

		public PlanningFrontier CreateFrontier(
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
			RecipeResolution? resolutionEvidence) =>
			new(
				kind,
				blockingRecipe,
				requirement,
				item,
				requiredQuantity,
				availableQuantity,
				deficitQuantity,
				depth,
				dependencyPath,
				candidateProducers,
				resolutionEvidence,
				Request.TargetSubgraph);
	}

	private sealed class Attempt
	{
		private Attempt(
			bool succeeded,
			RecursivePlanStatus status,
			VirtualResourceLedger ledger,
			IReadOnlyList<PlannedCraftStep> steps,
			IReadOnlyList<VirtualResourceReservation> reservations,
			IReadOnlyList<VirtualResourceProduction> productions,
			PlanningFrontier? frontier,
			IReadOnlyList<AlternativeDecision> decisions,
			IReadOnlyList<RecipeChoiceApplication> appliedChoices)
		{
			Succeeded = succeeded;
			Status = status;
			Ledger = ledger;
			Steps = steps;
			Reservations = reservations;
			Productions = productions;
			Frontier = frontier;
			Decisions = decisions;
			AppliedChoices = appliedChoices;
		}

		public bool Succeeded { get; }
		public RecursivePlanStatus Status { get; }
		public VirtualResourceLedger Ledger { get; }
		public IReadOnlyList<PlannedCraftStep> Steps { get; }
		public IReadOnlyList<VirtualResourceReservation> Reservations { get; }
		public IReadOnlyList<VirtualResourceProduction> Productions { get; }
		public PlanningFrontier? Frontier { get; }
		public IReadOnlyList<AlternativeDecision> Decisions { get; }
		public IReadOnlyList<RecipeChoiceApplication> AppliedChoices { get; }
		public AlternativeDecision? PendingDecision => Decisions.LastOrDefault(decision => !decision.IsResolved);

		public static Attempt Success(
			VirtualResourceLedger ledger,
			IReadOnlyList<PlannedCraftStep> steps,
			IReadOnlyList<VirtualResourceReservation> reservations,
			IReadOnlyList<VirtualResourceProduction> productions,
			IReadOnlyList<AlternativeDecision>? decisions = null,
			IReadOnlyList<RecipeChoiceApplication>? appliedChoices = null) =>
			new(
				true,
				RecursivePlanStatus.Success,
				ledger,
				steps,
				reservations,
				productions,
				frontier: null,
				decisions ?? Array.Empty<AlternativeDecision>(),
				appliedChoices ?? Array.Empty<RecipeChoiceApplication>());

		public static Attempt Failure(
			RecursivePlanStatus status,
			VirtualResourceLedger ledger,
			PlanningFrontier? frontier) =>
			new(
				false,
				status,
				ledger,
				Array.Empty<PlannedCraftStep>(),
				Array.Empty<VirtualResourceReservation>(),
				Array.Empty<VirtualResourceProduction>(),
				frontier,
				Array.Empty<AlternativeDecision>(),
				Array.Empty<RecipeChoiceApplication>());

		public Attempt WithDecision(AlternativeDecision decision, RecipeChoiceApplication? application = null) =>
			new(
				Succeeded,
				Status,
				Ledger,
				Steps,
				Reservations,
				Productions,
				Frontier,
				Decisions.Append(decision).ToArray(),
				application is null ? AppliedChoices : AppliedChoices.Append(application).ToArray());

		public Attempt WithStatus(RecursivePlanStatus status) =>
			new(
				false,
				status,
				Ledger,
				Array.Empty<PlannedCraftStep>(),
				Array.Empty<VirtualResourceReservation>(),
				Array.Empty<VirtualResourceProduction>(),
				Frontier,
				Decisions,
				AppliedChoices);

		public Attempt WithPrependedProvenance(
			IReadOnlyList<AlternativeDecision> decisions,
			IReadOnlyList<RecipeChoiceApplication> appliedChoices) =>
			new(
				Succeeded,
				Status,
				Ledger,
				Steps,
				Reservations,
				Productions,
				Frontier,
				decisions.Concat(Decisions).ToArray(),
				appliedChoices.Concat(AppliedChoices).ToArray());
	}
}
