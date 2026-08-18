using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Graph;
using Terraria;

namespace GraphCraft.Systems;

internal static class CraftGraphBuilder
{
	public static CraftGraph Build(IEnumerable<Recipe> recipes)
	{
		var graph = new CraftGraph();

		foreach (Recipe recipe in recipes) {
			if (recipe is null || recipe.Disabled || recipe.createItem.IsAir)
				continue;

			RecipeConstraintSet constraints = CreateConstraints(recipe);
			RecipeNode recipeNode = graph.AddRecipe(
				recipe.RecipeIndex,
				recipe.createItem.type,
				recipe.createItem.stack,
				constraints);

			IReadOnlyList<RecipeGroupInfo> groups = SnapshotRecipeGroups(recipe);
			foreach (Item ingredient in recipe.requiredItem) {
				IReadOnlyList<RecipeGroupInfo> matchingGroups = groups
					.Where(group => group.AcceptedItemTypes.Contains(ingredient.type))
					.ToArray();

				graph.AddIngredient(
					recipeNode,
					ingredient.type,
					ingredient.stack,
					matchingGroups);
			}
		}

		graph.Seal();
		return graph;
	}

	private static RecipeConstraintSet CreateConstraints(Recipe recipe)
	{
		int[] requiredTiles = recipe.requiredTile
			.Where(tileType => tileType >= 0)
			.Distinct()
			.ToArray();

		RecipeConditionInfo[] conditions = recipe.Conditions
			.Select(condition => new RecipeConditionInfo(
				condition.Description.Key,
				condition.Description.Value))
			.ToArray();

		return new RecipeConstraintSet(requiredTiles, conditions);
	}

	private static IReadOnlyList<RecipeGroupInfo> SnapshotRecipeGroups(Recipe recipe)
	{
		var groups = new List<RecipeGroupInfo>();

		foreach (int groupId in recipe.acceptedGroups.Distinct()) {
			if (!RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup? group))
				throw new InvalidOperationException($"Recipe {recipe.RecipeIndex} references unknown RecipeGroup {groupId}.");

			groups.Add(new RecipeGroupInfo(
				group.RegisteredId,
				group.IconicItemId,
				group.ValidItems.OrderBy(itemType => itemType).ToArray(),
				group.GetText()));
		}

		return groups;
	}
}
