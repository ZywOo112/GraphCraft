using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using Terraria;

namespace GraphCraft.Systems;

public static class PlayerCraftContextCapture
{
	public static PlayerCraftContextSnapshot Capture(Player player, CraftGraph graph)
	{
		ArgumentNullException.ThrowIfNull(player);
		ArgumentNullException.ThrowIfNull(graph);
		if (!graph.IsSealed)
			throw new InvalidOperationException("Player crafting context capture requires a sealed CraftGraph.");

		long captureStartTimestamp = Stopwatch.GetTimestamp();
		var inventoryCounts = new Dictionary<int, int>();
		foreach (Item item in player.inventory) {
			if (item is null || item.IsAir || item.stack <= 0)
				continue;
			inventoryCounts[item.type] = inventoryCounts.GetValueOrDefault(item.type) + item.stack;
		}

		bool[] adjacentTiles = player.adjTile;
		var availableTileTypes = new List<int>();
		for (int tileType = 0; tileType < adjacentTiles.Length; tileType++) {
			if (adjacentTiles[tileType])
				availableTileTypes.Add(tileType);
		}

		var conditionEvidence = new List<CapturedConditionEvidence>();
		foreach (RecipeNode recipe in graph.RecipesByIndex.Values.OrderBy(recipe => recipe.RecipeIndex)) {
			foreach (RecipeConditionInfo condition in recipe.Constraints.Conditions) {
				var key = new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex);
				conditionEvidence.Add(EvaluateCondition(key, condition));
			}
		}

		return PlayerCraftContextSnapshot.CreateCaptured(
			inventoryCounts,
			availableTileTypes,
			conditionEvidence,
			player.whoAmI,
			captureStartTimestamp);
	}

	private static CapturedConditionEvidence EvaluateCondition(
		RecipeConditionKey key,
		RecipeConditionInfo condition)
	{
		if (condition.Evaluator is null) {
			return new CapturedConditionEvidence(
				key,
				condition.Description,
				CapturedConditionState.Unknown,
				"The materialized condition has no safe evaluator.");
		}

		try {
			return new CapturedConditionEvidence(
				key,
				condition.Description,
				condition.Evaluator() ? CapturedConditionState.Satisfied : CapturedConditionState.Unsatisfied);
		}
		catch (Exception exception) {
			return new CapturedConditionEvidence(
				key,
				condition.Description,
				CapturedConditionState.Unknown,
				$"{exception.GetType().Name}: {exception.Message}");
		}
	}
}
