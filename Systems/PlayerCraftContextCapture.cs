using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resources;
using GraphCraft.Systems.Adapters;
using Terraria;

namespace GraphCraft.Systems;

public static class PlayerCraftContextCapture
{
	public static PlayerCraftContextSnapshot Capture(
		Player player,
		CraftGraph graph,
		IEnumerable<ICraftResourceSource>? additionalResourceSources = null,
		IEnumerable<ICraftEnvironmentSource>? additionalEnvironmentSources = null)
	{
		ArgumentNullException.ThrowIfNull(player);
		ArgumentNullException.ThrowIfNull(graph);
		if (!graph.IsSealed)
			throw new InvalidOperationException("Player crafting context capture requires a sealed CraftGraph.");

		long captureStartTimestamp = Stopwatch.GetTimestamp();
		var resourceSources = new List<ICraftResourceSource> { new VanillaPlayerInventoryResourceSource(player) };
		if (additionalResourceSources is not null)
			resourceSources.AddRange(additionalResourceSources);
		CraftResourceSourceSnapshot[] resourceSnapshots = resourceSources.Select(source => {
			CraftResourceSourceSnapshot snapshot = source.Capture();
			if (snapshot.Descriptor != source.Descriptor)
				throw new InvalidOperationException($"Resource source {source.Descriptor.SourceId} returned a snapshot with a different descriptor.");
			return snapshot;
		}).ToArray();

		var environmentSources = new List<ICraftEnvironmentSource> { new VanillaPlayerEnvironmentSource(player) };
		if (additionalEnvironmentSources is not null)
			environmentSources.AddRange(additionalEnvironmentSources);
		CraftEnvironmentSnapshot environment = CraftEnvironmentSnapshot.Merge(
			environmentSources.Select(source => source.Capture()));

		var conditionEvidence = new List<CapturedConditionEvidence>();
		foreach (RecipeNode recipe in graph.RecipesByIndex.Values.OrderBy(recipe => recipe.RecipeIndex)) {
			foreach (RecipeConditionInfo condition in recipe.Constraints.Conditions) {
				var key = new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex);
				conditionEvidence.Add(EvaluateCondition(key, condition));
			}
		}

		return PlayerCraftContextSnapshot.CreateCaptured(
			resourceSnapshots,
			environment,
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
