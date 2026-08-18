namespace GraphCraft.Core.Graph;

public sealed record CraftGraphStatistics(
	int ItemNodeCount,
	int RecipeNodeCount,
	int IngredientEdgeCount,
	int ProductEdgeCount,
	int AtomicItemCount,
	int DerivedItemCount,
	int ItemsWithMultipleProducerRecipes,
	int MaximumProducerRecipeCount,
	double AverageIngredientsPerRecipe,
	int RecipesWithConditions,
	int RecipesWithRequiredTiles);
