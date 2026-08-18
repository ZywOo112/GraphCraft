using System;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Planning;

public sealed record RecipePlanningQuantities(
	long RequestedOutputQuantity,
	long CraftRepetitions,
	long ProducedQuantity)
{
	public long ScaleIngredient(IngredientEdge ingredient)
	{
		ArgumentNullException.ThrowIfNull(ingredient);
		return checked((long)ingredient.RequiredAmount * CraftRepetitions);
	}
}

public static class RecipePlanningMath
{
	public static RecipePlanningQuantities Calculate(RecipeNode recipe, long requestedOutputQuantity)
	{
		ArgumentNullException.ThrowIfNull(recipe);
		if (requestedOutputQuantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(requestedOutputQuantity), "Requested output quantity must be positive.");
		if (recipe.OutputStack <= 0)
			throw new InvalidOperationException("A concrete recipe must have a positive output stack.");

		long repetitions = ((long)requestedOutputQuantity + recipe.OutputStack - 1L) / recipe.OutputStack;
		return new RecipePlanningQuantities(
			requestedOutputQuantity,
			repetitions,
			checked(repetitions * recipe.OutputStack));
	}
}
