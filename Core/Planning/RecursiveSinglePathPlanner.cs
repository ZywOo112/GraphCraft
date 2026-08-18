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
				availableQuantity: request.Ledger.GetBalance(request.SelectedRootRecipe.Result.ItemType).RemainingQuantity,
				deficitQuantity: request.RequiredOutputQuantity,
				depth: 0,
				Array.Empty<PlanningPathEntry>(),
				Array.Empty<RecipeNode>(),
				resolutionEvidence: null);
			return FailureResult(request, RecursivePlanStatus.RecipeOutsideTargetSubgraph, frontier);
		}

		Attempt attempt = PlanRecipe(
			context,
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			request.Ledger,
			depth: 0,
			Array.Empty<PlanningPathEntry>());
		if (!attempt.Succeeded)
			return FailureResult(request, attempt.Status, attempt.Frontier!);

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
			frontier: null);
	}

	private static Attempt PlanRecipe(
		PlanningContext context,
		RecipeNode recipe,
		long requestedOutputQuantity,
		VirtualResourceLedger inputLedger,
		int depth,
		IReadOnlyList<PlanningPathEntry> dependencyPath)
	{
		var currentEntry = new PlanningPathEntry(depth, recipe, requestedOutputQuantity);
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
						inputLedger);
				}

				if (producers.Length > 1) {
					return context.Failure(
						RecursivePlanStatus.AlternativeProducerDeferred,
						PlanningFrontierKind.AlternativeProducerDeferred,
						recipe,
						ingredient,
						ingredient.Source,
						required,
						availableBeforeExpansion,
						deficit,
						depth,
						currentPath,
						producers,
						resolutionEvidence: null,
						inputLedger);
				}

				Attempt child = PlanRecipe(context, producers[0], deficit, trialLedger, depth + 1, currentPath);
				if (!child.Succeeded)
					return child;

				trialLedger = child.Ledger;
				steps.AddRange(child.Steps);
				reservations.AddRange(child.Reservations);
				productions.AddRange(child.Productions);
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
		return Attempt.Success(trialLedger, steps, reservations, productions);
	}

	private static RecursiveRecipePlanResult FailureResult(
		RecursiveRecipePlanningRequest request,
		RecursivePlanStatus status,
		PlanningFrontier frontier) =>
		new(
			request.SelectedRootRecipe,
			request.RequiredOutputQuantity,
			status,
			request.Ledger,
			request.Ledger,
			Array.Empty<PlannedCraftStep>(),
			Array.Empty<VirtualResourceReservation>(),
			Array.Empty<VirtualResourceProduction>(),
			frontier);

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
			PlanningFrontier? frontier)
		{
			Succeeded = succeeded;
			Status = status;
			Ledger = ledger;
			Steps = steps;
			Reservations = reservations;
			Productions = productions;
			Frontier = frontier;
		}

		public bool Succeeded { get; }
		public RecursivePlanStatus Status { get; }
		public VirtualResourceLedger Ledger { get; }
		public IReadOnlyList<PlannedCraftStep> Steps { get; }
		public IReadOnlyList<VirtualResourceReservation> Reservations { get; }
		public IReadOnlyList<VirtualResourceProduction> Productions { get; }
		public PlanningFrontier? Frontier { get; }

		public static Attempt Success(
			VirtualResourceLedger ledger,
			IReadOnlyList<PlannedCraftStep> steps,
			IReadOnlyList<VirtualResourceReservation> reservations,
			IReadOnlyList<VirtualResourceProduction> productions) =>
			new(true, RecursivePlanStatus.Success, ledger, steps, reservations, productions, frontier: null);

		public static Attempt Failure(
			RecursivePlanStatus status,
			VirtualResourceLedger ledger,
			PlanningFrontier frontier) =>
			new(
				false,
				status,
				ledger,
				Array.Empty<PlannedCraftStep>(),
				Array.Empty<VirtualResourceReservation>(),
				Array.Empty<VirtualResourceProduction>(),
				frontier);
	}
}
