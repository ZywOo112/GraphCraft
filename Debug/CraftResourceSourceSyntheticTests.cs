using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using GraphCraft.Core.Subgraphs;
using Terraria.ID;

namespace GraphCraft.Debug;

internal sealed record CraftResourceSourceSyntheticReport(
	int PassedTestCount,
	int MultiSourceAggregateCount,
	TimeSpan Elapsed);

internal static class CraftResourceSourceSyntheticTests
{
	public static CraftResourceSourceSyntheticReport Run(
		CraftGraph graph,
		TargetCraftSubgraph furnaceSubgraph)
	{
		var timer = Stopwatch.StartNew();
		RecipeNode furnaceRecipe = furnaceSubgraph.Target.ProducerRecipes
			.First(recipe => recipe.Ingredients.Any(edge => edge.Source.ItemType == ItemID.StoneBlock));
		IngredientEdge stoneRequirement = furnaceRecipe.Ingredients
			.Single(edge => edge.Source.ItemType == ItemID.StoneBlock);
		IngredientEdge woodGroupRequirement = furnaceRecipe.Ingredients
			.Single(edge => edge.AcceptsRecipeGroup);

		var singleSource = new FakeResourceSource(
			"test:single",
			CraftResourceSourceKind.Inventory,
			(ItemID.StoneBlock, 30));
		PlayerCraftContextSnapshot singleContext = Context(singleSource);
		IngredientResolution singleResolution = ResolutionOverlayBuilder.ResolveIngredientRequirement(stoneRequirement, singleContext);
		Assert(singleResolution.AvailableAmount == 30 && singleResolution.LocallySatisfied,
			"Test A single source compatibility failed.");

		var sourceA = new FakeResourceSource("test:source-a", (ItemID.StoneBlock, 3));
		var sourceB = new FakeResourceSource("test:source-b", (ItemID.StoneBlock, 7));
		PlayerCraftContextSnapshot aggregateContext = Context(sourceA, sourceB);
		Assert(aggregateContext.GetAvailableCount(ItemID.StoneBlock) == 10,
			"Test B multiple source aggregation did not produce 10.");

		IReadOnlyList<CraftResourceEvidence> stoneEvidence = aggregateContext.GetResourceEvidence(ItemID.StoneBlock);
		Assert(stoneEvidence.Count == 2 &&
			stoneEvidence.Any(evidence => evidence.SourceId.Value == "test:source-a" && evidence.Count == 3) &&
			stoneEvidence.Any(evidence => evidence.SourceId.Value == "test:source-b" && evidence.Count == 7),
			"Test C source provenance was not preserved.");

		IngredientEdge splitRequirement = SyntheticRequirement(graph, ItemID.StoneBlock, requiredAmount: 8);
		IngredientResolution splitResolution = ResolutionOverlayBuilder.ResolveIngredientRequirement(splitRequirement, aggregateContext);
		Assert(splitResolution.AvailableAmount == 10 && splitResolution.LocallySatisfied && splitResolution.MatchedResources.Count == 2,
			"Test D split-across-sources requirement was not satisfied.");

		PlayerCraftContextSnapshot insufficientContext = Context(
			new FakeResourceSource("test:short-a", (ItemID.StoneBlock, 3)),
			new FakeResourceSource("test:short-b", (ItemID.StoneBlock, 2)));
		IngredientResolution insufficient = ResolutionOverlayBuilder.ResolveIngredientRequirement(splitRequirement, insufficientContext);
		Assert(insufficient.AvailableAmount == 5 && !insufficient.LocallySatisfied,
			"Test E insufficient aggregate was not detected.");

		int[] acceptedAlternatives = woodGroupRequirement.AcceptedRecipeGroups
			.SelectMany(group => group.AcceptedItemTypes)
			.Where(itemType => itemType != woodGroupRequirement.Source.ItemType)
			.Distinct()
			.Take(2)
			.ToArray();
		Assert(acceptedAlternatives.Length == 2, "Test F requires two real Any Wood alternatives.");
		var groupA = new FakeResourceSource("test:wood-a", (acceptedAlternatives[0], 3));
		var groupB = new FakeResourceSource("test:wood-b", (acceptedAlternatives[1], 7));
		IngredientEdge groupRequirement = SyntheticRequirement(
			graph,
			woodGroupRequirement.Source.ItemType,
			requiredAmount: 8,
			woodGroupRequirement.AcceptedRecipeGroups);
		IngredientResolution groupResolution = ResolutionOverlayBuilder.ResolveIngredientRequirement(
			groupRequirement,
			Context(groupA, groupB));
		Assert(groupResolution.ResolutionMode == IngredientResolutionMode.RecipeGroup &&
			groupResolution.AvailableAmount == 10 &&
			groupResolution.LocallySatisfied &&
			groupResolution.MatchedResources.Select(evidence => evidence.SourceId).Distinct().Count() == 2 &&
			groupResolution.MatchedResources.Select(evidence => evidence.ItemType).Distinct().Count() == 2,
			"Test F multi-source Recipe Group aggregation/provenance failed.");

		var mutableSource = new FakeResourceSource("test:isolation", (ItemID.StoneBlock, 3));
		PlayerCraftContextSnapshot oldContext = Context(mutableSource);
		mutableSource.SetCount(ItemID.StoneBlock, 99);
		Assert(oldContext.GetAvailableCount(ItemID.StoneBlock) == 3 &&
			Context(mutableSource).GetAvailableCount(ItemID.StoneBlock) == 99,
			"Test G source mutation polluted an existing immutable snapshot.");

		timer.Stop();
		return new CraftResourceSourceSyntheticReport(7, aggregateContext.GetAvailableCount(ItemID.StoneBlock), timer.Elapsed);
	}

	private static PlayerCraftContextSnapshot Context(params ICraftResourceSource[] sources) =>
		PlayerCraftContextSnapshot.CreateSynthetic(sources.Select(source => source.Capture()));

	private static IngredientEdge SyntheticRequirement(
		CraftGraph graph,
		int itemType,
		int requiredAmount,
		IReadOnlyList<RecipeGroupInfo>? groups = null)
	{
		ItemNode source = graph.ItemsByType[itemType];
		RecipeNode resultRecipe = graph.RecipesByIndex.Values.OrderBy(recipe => recipe.RecipeIndex).First();
		var constraints = new RecipeConstraintSet(Array.Empty<int>(), Array.Empty<RecipeConditionInfo>());
		var recipe = new RecipeNode(-20000 - requiredAmount, resultRecipe.Result, 1, constraints);
		return new IngredientEdge(0, source, recipe, requiredAmount, groups ?? Array.Empty<RecipeGroupInfo>());
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"Craft resource source synthetic validation failed: {message}");
	}

	private sealed class FakeResourceSource : ICraftResourceSource
	{
		private readonly Dictionary<int, int> _counts;

		public FakeResourceSource(string sourceId, params (int ItemType, int Count)[] counts)
			: this(sourceId, CraftResourceSourceKind.Synthetic, counts)
		{
		}

		public FakeResourceSource(
			string sourceId,
			CraftResourceSourceKind sourceKind,
			params (int ItemType, int Count)[] counts)
		{
			Descriptor = new CraftResourceSourceDescriptor(
				new CraftResourceSourceId(sourceId),
				sourceKind,
				sourceId,
				CraftResourceConsumptionCapability.EvidenceOnly);
			_counts = counts.ToDictionary(pair => pair.ItemType, pair => pair.Count);
		}

		public CraftResourceSourceDescriptor Descriptor { get; }

		public CraftResourceSourceSnapshot Capture() => new(Descriptor, _counts);

		public void SetCount(int itemType, int count) => _counts[itemType] = count;
	}
}
