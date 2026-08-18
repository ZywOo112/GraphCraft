using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphCraft.Core.Graph;

namespace GraphCraft.Debug;

internal static class CraftGraphDiagnostics
{
	public static string FormatStatistics(CraftGraphStatistics statistics)
	{
		return $"""
		[GraphCraft] Materialized CraftGraph statistics
		  ItemNodes: {statistics.ItemNodeCount}
		  RecipeNodes: {statistics.RecipeNodeCount}
		  IngredientEdges: {statistics.IngredientEdgeCount}
		  ProductEdges: {statistics.ProductEdgeCount}
		  AtomicItems: {statistics.AtomicItemCount}
		  DerivedItems: {statistics.DerivedItemCount}
		  ItemsWithMultipleProducers: {statistics.ItemsWithMultipleProducerRecipes}
		  MaximumProducerRecipeCount: {statistics.MaximumProducerRecipeCount}
		  AverageIngredientsPerRecipe: {statistics.AverageIngredientsPerRecipe:F2}
		  RecipesWithConditions: {statistics.RecipesWithConditions}
		  RecipesWithRequiredTiles: {statistics.RecipesWithRequiredTiles}
		""";
	}

	public static string FormatTargetInspection(
		CraftGraph graph,
		int targetItemType,
		Func<int, string> itemName,
		Func<int, string> tileName)
	{
		if (!graph.TryGetItemNode(targetItemType, out ItemNode? target) || target is null)
			return $"[GraphCraft] Target ItemNode {targetItemType} was not found.";

		var builder = new StringBuilder();
		builder.AppendLine($"[GraphCraft] Target inspection: {DescribeItem(target, itemName)}");

		foreach (RecipeNode recipe in target.ProducerRecipes) {
			builder.AppendLine($"  -> RecipeNode #{recipe.RecipeIndex} (output x{recipe.OutputStack})");
			foreach (IngredientEdge ingredient in recipe.Ingredients)
				builder.AppendLine($"     <- {DescribeIngredient(ingredient, itemName)}");

			builder.AppendLine($"     required tiles: {DescribeTiles(recipe, tileName)}");
			builder.AppendLine($"     conditions: {DescribeConditions(recipe)}");
		}

		return builder.ToString();
	}

	public static string FormatDependencyGraph(
		CraftGraph graph,
		int targetItemType,
		int maximumRecipeDepth,
		Func<int, string> itemName)
	{
		if (!graph.TryGetItemNode(targetItemType, out ItemNode? target) || target is null)
			return $"[GraphCraft] Target ItemNode {targetItemType} was not found.";

		var builder = new StringBuilder();
		var visited = new HashSet<string>(StringComparer.Ordinal);
		builder.AppendLine($"[GraphCraft] Dependency graph (recipe depth <= {maximumRecipeDepth})");
		AppendItem(builder, target, string.Empty, isLast: true, recipeDepth: 0, maximumRecipeDepth, itemName, visited, isRoot: true);
		return builder.ToString();
	}

	private static void AppendItem(
		StringBuilder builder,
		ItemNode item,
		string prefix,
		bool isLast,
		int recipeDepth,
		int maximumRecipeDepth,
		Func<int, string> itemName,
		HashSet<string> visited,
		bool isRoot = false)
	{
		string connector = isRoot ? string.Empty : isLast ? "└─ " : "├─ ";
		bool firstVisit = visited.Add(item.GraphId);
		builder.Append(prefix).Append(connector).Append(DescribeItem(item, itemName));
		if (!firstVisit)
			builder.Append(" [already visited]");
		builder.AppendLine();

		if (!firstVisit || recipeDepth >= maximumRecipeDepth)
			return;

		string childPrefix = isRoot ? string.Empty : prefix + (isLast ? "   " : "│  ");
		for (int recipeIndex = 0; recipeIndex < item.ProducerRecipes.Count; recipeIndex++) {
			RecipeNode recipe = item.ProducerRecipes[recipeIndex];
			bool recipeIsLast = recipeIndex == item.ProducerRecipes.Count - 1;
			AppendRecipe(builder, recipe, childPrefix, recipeIsLast, recipeDepth, maximumRecipeDepth, itemName, visited);
		}
	}

	private static void AppendRecipe(
		StringBuilder builder,
		RecipeNode recipe,
		string prefix,
		bool isLast,
		int recipeDepth,
		int maximumRecipeDepth,
		Func<int, string> itemName,
		HashSet<string> visited)
	{
		string connector = isLast ? "└─ " : "├─ ";
		bool firstVisit = visited.Add(recipe.GraphId);
		builder.Append(prefix).Append(connector)
			.Append($"Recipe #{recipe.RecipeIndex} -> x{recipe.OutputStack}");
		if (!firstVisit)
			builder.Append(" [already visited]");
		builder.AppendLine();

		if (!firstVisit)
			return;

		string childPrefix = prefix + (isLast ? "   " : "│  ");
		for (int ingredientIndex = 0; ingredientIndex < recipe.Ingredients.Count; ingredientIndex++) {
			IngredientEdge ingredient = recipe.Ingredients[ingredientIndex];
			bool ingredientIsLast = ingredientIndex == recipe.Ingredients.Count - 1;
			AppendItem(
				builder,
				ingredient.Source,
				childPrefix,
				ingredientIsLast,
				recipeDepth + 1,
				maximumRecipeDepth,
				itemName,
				visited);
		}
	}

	private static string DescribeItem(ItemNode item, Func<int, string> itemName) =>
		$"{itemName(item.ItemType)} (ItemID {item.ItemType}) [{item.Classification}]";

	private static string DescribeIngredient(IngredientEdge ingredient, Func<int, string> itemName)
	{
		string groupText = ingredient.AcceptsRecipeGroup
			? " groups=" + string.Join(", ", ingredient.AcceptedRecipeGroups.Select(group =>
				$"#{group.GroupId} '{group.DisplayText}' ({group.AcceptedItemTypes.Count} items)"))
			: string.Empty;

		return $"{itemName(ingredient.Source.ItemType)} (ItemID {ingredient.Source.ItemType}) x{ingredient.RequiredAmount}{groupText}";
	}

	private static string DescribeTiles(RecipeNode recipe, Func<int, string> tileName) =>
		recipe.Constraints.RequiredTileTypes.Count == 0
			? "none"
			: string.Join(", ", recipe.Constraints.RequiredTileTypes.Select(tileType => $"{tileName(tileType)} ({tileType})"));

	private static string DescribeConditions(RecipeNode recipe) =>
		recipe.Constraints.Conditions.Count == 0
			? "none"
			: string.Join("; ", recipe.Constraints.Conditions.Select(condition =>
				$"{condition.Description} [{condition.LocalizationKey}]"));
}
