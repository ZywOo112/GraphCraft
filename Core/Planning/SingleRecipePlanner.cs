using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Core.Planning;

public static class SingleRecipePlanner
{
	public static SingleRecipePlanResult Plan(SingleRecipePlanningRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		RecipeNode recipe = request.SelectedRecipe;
		if (!request.TargetSubgraph.RecipeNodes.Any(candidate => ReferenceEquals(candidate, recipe))) {
			return Result(
				request,
				SingleRecipePlanStatus.RecipeOutsideTargetSubgraph,
				repetitions: 0,
				producedQuantity: 0,
				Array.Empty<IngredientPlanResult>(),
				Array.Empty<VirtualResourceReservation>(),
				request.Ledger,
				resolutionEvidence: null);
		}

		long repetitions = DivideRoundUp(request.RequiredOutputQuantity, recipe.OutputStack);
		long producedQuantity = checked(repetitions * recipe.OutputStack);

		RecipeResolution evidence = request.Resolution.GetRecipeResolution(recipe);
		SingleRecipePlanStatus? blockedStatus = evidence.State switch
		{
			RecipeLocalState.BlockedByStation => SingleRecipePlanStatus.BlockedByStation,
			RecipeLocalState.BlockedByCondition => SingleRecipePlanStatus.BlockedByCondition,
			RecipeLocalState.UnknownConstraint => SingleRecipePlanStatus.UnknownConstraint,
			_ => null
		};
		if (blockedStatus is SingleRecipePlanStatus blocked) {
			return Result(
				request,
				blocked,
				repetitions,
				producedQuantity,
				CreateNotAttemptedResults(recipe, repetitions, request.Ledger),
				Array.Empty<VirtualResourceReservation>(),
				request.Ledger,
				evidence);
		}

		IngredientEdge[] orderedIngredients = recipe.Ingredients
			.OrderBy(ingredient => ingredient.Ordinal)
			.ThenBy(ingredient => ingredient.Source.ItemType)
			.ToArray();
		if (orderedIngredients.Any(ingredient => ingredient.AcceptsRecipeGroup)) {
			IngredientPlanResult[] deferred = orderedIngredients.Select(ingredient => {
				long required = checked((long)ingredient.RequiredAmount * repetitions);
				int available = request.Ledger.GetBalance(ingredient.Source.ItemType).RemainingQuantity;
				return new IngredientPlanResult(
					ingredient,
					ingredient.AcceptsRecipeGroup ? IngredientPlanStatus.DeferredRecipeGroup : IngredientPlanStatus.NotAttempted,
					required,
					available,
					ReservedQuantity: 0,
					RemainingAfter: available,
					ShortageQuantity: 0);
			}).ToArray();
			return Result(
				request,
				SingleRecipePlanStatus.DeferredRecipeGroupAllocation,
				repetitions,
				producedQuantity,
				deferred,
				Array.Empty<VirtualResourceReservation>(),
				request.Ledger,
				evidence);
		}

		VirtualResourceLedger trialLedger = request.Ledger;
		var ingredientResults = new List<IngredientPlanResult>(orderedIngredients.Length);
		var reservations = new List<VirtualResourceReservation>(orderedIngredients.Length);
		for (int index = 0; index < orderedIngredients.Length; index++) {
			IngredientEdge ingredient = orderedIngredients[index];
			long required = checked((long)ingredient.RequiredAmount * repetitions);
			VirtualReservationAttempt attempt = trialLedger.TryReserveExact(ingredient.Source.ItemType, required);
			if (!attempt.Succeeded) {
				ingredientResults.Add(new IngredientPlanResult(
					ingredient,
					IngredientPlanStatus.Insufficient,
					required,
					attempt.AvailableQuantity,
					ReservedQuantity: 0,
					RemainingAfter: attempt.AvailableQuantity,
					attempt.ShortageQuantity));
				for (int remainingIndex = index + 1; remainingIndex < orderedIngredients.Length; remainingIndex++) {
					IngredientEdge unattempted = orderedIngredients[remainingIndex];
					int available = request.Ledger.GetBalance(unattempted.Source.ItemType).RemainingQuantity;
					ingredientResults.Add(new IngredientPlanResult(
						unattempted,
						IngredientPlanStatus.NotAttempted,
						checked((long)unattempted.RequiredAmount * repetitions),
						available,
						ReservedQuantity: 0,
						RemainingAfter: available,
						ShortageQuantity: 0));
				}

				IngredientPlanResult[] atomicResults = ingredientResults.Select(result =>
					result.Status == IngredientPlanStatus.Reserved
						? result with {
							Status = IngredientPlanStatus.StagedThenDiscarded,
							ReservedQuantity = 0,
							RemainingAfter = request.Ledger.GetBalance(result.Ingredient.Source.ItemType).RemainingQuantity
						}
						: result).ToArray();
				return Result(
					request,
					SingleRecipePlanStatus.InsufficientDirectResources,
					repetitions,
					producedQuantity,
					atomicResults,
					Array.Empty<VirtualResourceReservation>(),
					request.Ledger,
					evidence);
			}

			VirtualResourceReservation reservation = attempt.Reservation
				?? throw new InvalidOperationException("A successful virtual reservation did not return reservation evidence.");
			trialLedger = attempt.Ledger;
			reservations.Add(reservation);
			ingredientResults.Add(new IngredientPlanResult(
				ingredient,
				IngredientPlanStatus.Reserved,
				required,
				attempt.AvailableQuantity,
				reservation.Quantity,
				reservation.RemainingAfter,
				ShortageQuantity: 0));
		}

		return Result(
			request,
			SingleRecipePlanStatus.Success,
			repetitions,
			producedQuantity,
			ingredientResults.ToArray(),
			reservations.ToArray(),
			trialLedger,
			evidence);
	}

	private static long DivideRoundUp(int requiredOutputQuantity, int outputStack)
	{
		if (outputStack <= 0)
			throw new InvalidOperationException("A concrete recipe must have a positive output stack.");
		return ((long)requiredOutputQuantity + outputStack - 1L) / outputStack;
	}

	private static IngredientPlanResult[] CreateNotAttemptedResults(
		RecipeNode recipe,
		long repetitions,
		VirtualResourceLedger ledger) =>
		recipe.Ingredients
			.OrderBy(ingredient => ingredient.Ordinal)
			.ThenBy(ingredient => ingredient.Source.ItemType)
			.Select(ingredient => {
				int available = ledger.GetBalance(ingredient.Source.ItemType).RemainingQuantity;
				return new IngredientPlanResult(
					ingredient,
					IngredientPlanStatus.NotAttempted,
					checked((long)ingredient.RequiredAmount * repetitions),
					AvailableBefore: available,
					ReservedQuantity: 0,
					RemainingAfter: available,
					ShortageQuantity: 0);
			})
			.ToArray();

	private static SingleRecipePlanResult Result(
		SingleRecipePlanningRequest request,
		SingleRecipePlanStatus status,
		long repetitions,
		long producedQuantity,
		IReadOnlyList<IngredientPlanResult> ingredientResults,
		IReadOnlyList<VirtualResourceReservation> reservations,
		VirtualResourceLedger resultLedger,
		RecipeResolution? resolutionEvidence) =>
		new(
			request.SelectedRecipe,
			status,
			request.RequiredOutputQuantity,
			repetitions,
			producedQuantity,
			ingredientResults,
			reservations,
			request.Ledger,
			resultLedger,
			resolutionEvidence);
}
