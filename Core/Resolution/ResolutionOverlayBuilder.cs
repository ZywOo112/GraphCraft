using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Core.Resolution;

public static class ResolutionOverlayBuilder
{
	public static ResolutionSnapshot Build(
		TargetCraftSubgraph subgraph,
		GraphAnalysisResult analysis,
		PlayerCraftContextSnapshot context)
	{
		ArgumentNullException.ThrowIfNull(subgraph);
		ArgumentNullException.ThrowIfNull(analysis);
		ArgumentNullException.ThrowIfNull(context);

		var timer = Stopwatch.StartNew();
		var itemResolutions = new Dictionary<ItemNode, ItemResolution>();
		foreach (ItemNode item in subgraph.ItemNodes) {
			int count = context.GetInventoryCount(item.ItemType);
			ItemResolutionState state = count > 0
				? ItemResolutionState.Owned
				: item.Classification == ItemClassification.Atomic
					? ItemResolutionState.UnownedAtomic
					: ItemResolutionState.UnownedDerived;
			StronglyConnectedComponent component = analysis.GetComponent(item);
			itemResolutions.Add(item, new ItemResolution(item, state, count, component.ComponentId, component.IsCyclic));
		}

		var ingredientResolutions = new Dictionary<IngredientEdge, IngredientResolution>();
		foreach (IngredientEdge ingredient in subgraph.IngredientEdges)
			ingredientResolutions.Add(ingredient, ResolveIngredientRequirement(ingredient, context));

		var recipeResolutions = new Dictionary<RecipeNode, RecipeResolution>();
		foreach (RecipeNode recipe in subgraph.RecipeNodes) {
			IngredientResolution[] ingredients = recipe.Ingredients
				.Where(ingredientResolutions.ContainsKey)
				.Select(ingredient => ingredientResolutions[ingredient])
				.ToArray();
			StationConstraintResolution station = ResolveStationConstraint(recipe, context);
			ConditionConstraintResolution[] conditions = ResolveConditions(recipe, context);
			RecipeLocalState state = DetermineRecipeState(ingredients, station, conditions);
			StronglyConnectedComponent component = analysis.GetComponent(recipe);
			recipeResolutions.Add(recipe, new RecipeResolution(
				recipe,
				state,
				new ReadOnlyCollection<IngredientResolution>(ingredients),
				station,
				new ReadOnlyCollection<ConditionConstraintResolution>(conditions),
				component.ComponentId,
				component.IsCyclic));
		}

		timer.Stop();
		var result = new ResolutionSnapshot(
			subgraph,
			context,
			itemResolutions,
			ingredientResolutions,
			recipeResolutions,
			timer.Elapsed);
		ValidateCoverage(result);
		return result;
	}

	internal static IngredientResolution ResolveIngredientRequirement(
		IngredientEdge ingredient,
		PlayerCraftContextSnapshot context)
	{
		if (!ingredient.AcceptsRecipeGroup) {
			int available = context.GetInventoryCount(ingredient.Source.ItemType);
			return new IngredientResolution(
				ingredient,
				ingredient.RequiredAmount,
				available,
				available >= ingredient.RequiredAmount,
				IngredientResolutionMode.DirectItem,
				Array.Empty<MatchedInventoryItem>());
		}

		int[] acceptedItemTypes = ingredient.AcceptedRecipeGroups
			.SelectMany(group => group.AcceptedItemTypes)
			.Distinct()
			.OrderBy(itemType => itemType)
			.ToArray();
		MatchedInventoryItem[] matchedItems = acceptedItemTypes
			.Select(itemType => new MatchedInventoryItem(itemType, context.GetInventoryCount(itemType)))
			.Where(evidence => evidence.Count > 0)
			.ToArray();
		int groupAvailable = matchedItems.Sum(evidence => evidence.Count);
		return new IngredientResolution(
			ingredient,
			ingredient.RequiredAmount,
			groupAvailable,
			groupAvailable >= ingredient.RequiredAmount,
			IngredientResolutionMode.RecipeGroup,
			new ReadOnlyCollection<MatchedInventoryItem>(matchedItems));
	}

	private static StationConstraintResolution ResolveStationConstraint(
		RecipeNode recipe,
		PlayerCraftContextSnapshot context)
	{
		int[] requiredTiles = recipe.Constraints.RequiredTileTypes.ToArray();
		int[] missingTiles = requiredTiles.Where(tileType => !context.IsTileAvailable(tileType)).ToArray();
		return new StationConstraintResolution(
			missingTiles.Length == 0 ? StationConstraintState.Satisfied : StationConstraintState.Unsatisfied,
			new ReadOnlyCollection<int>(requiredTiles),
			new ReadOnlyCollection<int>(missingTiles));
	}

	private static ConditionConstraintResolution[] ResolveConditions(
		RecipeNode recipe,
		PlayerCraftContextSnapshot context)
	{
		return recipe.Constraints.Conditions.Select(condition => {
			var key = new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex);
			if (!context.TryGetConditionEvidence(key, out CapturedConditionEvidence? evidence) || evidence is null) {
				return new ConditionConstraintResolution(
					key,
					condition.Description,
					ConditionConstraintState.Unknown,
					"No condition evidence was captured for this snapshot.");
			}

			ConditionConstraintState state = evidence.State switch
			{
				CapturedConditionState.Satisfied => ConditionConstraintState.Satisfied,
				CapturedConditionState.Unsatisfied => ConditionConstraintState.Unsatisfied,
				_ => ConditionConstraintState.Unknown
			};
			return new ConditionConstraintResolution(key, condition.Description, state, evidence.UnknownReason);
		}).ToArray();
	}

	private static RecipeLocalState DetermineRecipeState(
		IReadOnlyList<IngredientResolution> ingredients,
		StationConstraintResolution station,
		IReadOnlyList<ConditionConstraintResolution> conditions)
	{
		if (conditions.Any(condition => condition.State == ConditionConstraintState.Unknown))
			return RecipeLocalState.UnknownConstraint;
		if (conditions.Any(condition => condition.State == ConditionConstraintState.Unsatisfied))
			return RecipeLocalState.BlockedByCondition;
		if (!station.IsSatisfied)
			return RecipeLocalState.BlockedByStation;
		if (ingredients.Any(ingredient => !ingredient.LocallySatisfied))
			return RecipeLocalState.NeedsIngredients;
		return RecipeLocalState.ReadyNow;
	}

	private static void ValidateCoverage(ResolutionSnapshot result)
	{
		TargetCraftSubgraph subgraph = result.SourceSubgraph;
		if (result.ItemResolutions.Count != subgraph.ItemNodes.Count ||
			subgraph.ItemNodes.Any(item => !result.ItemResolutions.TryGetValue(item, out ItemResolution? value) || !ReferenceEquals(value.Item, item)))
			throw new InvalidOperationException("ResolutionSnapshot does not cover every canonical ItemNode exactly once.");

		if (result.IngredientResolutions.Count != subgraph.IngredientEdges.Count ||
			subgraph.IngredientEdges.Any(edge => !result.IngredientResolutions.TryGetValue(edge, out IngredientResolution? value) || !ReferenceEquals(value.Ingredient, edge)))
			throw new InvalidOperationException("ResolutionSnapshot does not cover every canonical IngredientEdge exactly once.");

		if (result.RecipeResolutions.Count != subgraph.RecipeNodes.Count ||
			subgraph.RecipeNodes.Any(recipe => !result.RecipeResolutions.TryGetValue(recipe, out RecipeResolution? value) || !ReferenceEquals(value.Recipe, recipe)))
			throw new InvalidOperationException("ResolutionSnapshot does not cover every canonical RecipeNode exactly once.");
	}
}
