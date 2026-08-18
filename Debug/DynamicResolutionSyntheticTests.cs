using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;
using Terraria.ID;

namespace GraphCraft.Debug;

internal sealed record DynamicResolutionSyntheticReport(
	int PassedScenarioCount,
	int ConditionRecipeIndex,
	int ConditionResultItemType,
	string ConditionDescription,
	TimeSpan SyntheticContextElapsed,
	TimeSpan FurnaceOverlayElapsed,
	TimeSpan CellPhoneOverlayElapsed,
	CraftResourceSourceSyntheticReport ResourceSourceReport,
	TimeSpan TotalElapsed);

internal static class DynamicResolutionSyntheticTests
{
	public static DynamicResolutionSyntheticReport Run(
		CraftGraph graph,
		GraphAnalysisResult analysis,
		TargetCraftSubgraph furnaceSubgraph,
		TargetCraftSubgraph cellPhoneSubgraph)
	{
		var totalTimer = Stopwatch.StartNew();
		CraftGraphStatistics beforeStatistics = graph.GetStatistics();
		RecipeNode furnaceRecipe = furnaceSubgraph.Target.ProducerRecipes
			.First(recipe => recipe.Ingredients.Any(edge => edge.Source.ItemType == ItemID.StoneBlock));
		IngredientEdge stoneRequirement = furnaceRecipe.Ingredients
			.Single(edge => edge.Source.ItemType == ItemID.StoneBlock);
		IngredientEdge woodGroupRequirement = furnaceRecipe.Ingredients
			.Single(edge => edge.AcceptsRecipeGroup && edge.AcceptedRecipeGroups
				.Any(group => group.AcceptedItemTypes.Contains(ItemID.BorealWood)));

		var mutableInventory = new Dictionary<int, int> { [ItemID.StoneBlock] = 30 };
		PlayerCraftContextSnapshot satisfiedContext = PlayerCraftContextSnapshot.CreateSynthetic(mutableInventory);
		mutableInventory[ItemID.StoneBlock] = 0;
		Assert(satisfiedContext.GetAvailableCount(ItemID.StoneBlock) == 30, "A snapshot changed after its source resource dictionary was mutated.");
		ResolutionSnapshot satisfiedOverlay = ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, satisfiedContext);
		IngredientResolution satisfiedStone = satisfiedOverlay.GetIngredientResolution(stoneRequirement);
		AssertIngredient(satisfiedStone, required: 20, available: 30, locallySatisfied: true, "Scenario 1");

		PlayerCraftContextSnapshot insufficientContext = SyntheticInventory((ItemID.StoneBlock, 8));
		ResolutionSnapshot insufficientOverlay = ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, insufficientContext);
		IngredientResolution insufficientStone = insufficientOverlay.GetIngredientResolution(stoneRequirement);
		AssertIngredient(insufficientStone, required: 20, available: 8, locallySatisfied: false, "Scenario 2");

		PlayerCraftContextSnapshot groupContext = SyntheticInventory((ItemID.BorealWood, 10));
		ResolutionSnapshot groupOverlay = ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, groupContext);
		IngredientResolution groupResolution = groupOverlay.GetIngredientResolution(woodGroupRequirement);
		AssertIngredient(groupResolution, required: 4, available: 10, locallySatisfied: true, "Scenario 3");
		Assert(groupResolution.ResolutionMode == IngredientResolutionMode.RecipeGroup, "Scenario 3 did not use RecipeGroup mode.");
		Assert(groupResolution.MatchedResources.Any(evidence =>
			evidence.ItemType == ItemID.BorealWood && evidence.Count == 10),
			"Scenario 3 did not preserve Boreal Wood x10 evidence.");

		PlayerCraftContextSnapshot missingStationContext = SyntheticInventory(
			(ItemID.StoneBlock, 20),
			(ItemID.BorealWood, 4),
			(ItemID.Torch, 3));
		ResolutionSnapshot missingStationOverlay = ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, missingStationContext);
		RecipeResolution missingStationRecipe = missingStationOverlay.GetRecipeResolution(furnaceRecipe);
		Assert(missingStationRecipe.State == RecipeLocalState.BlockedByStation, "Scenario 4 was not BlockedByStation.");
		Assert(missingStationRecipe.Station.MissingTileTypes.Contains(TileID.WorkBenches), "Scenario 4 did not report the missing Work Bench tile.");
		Assert(ResolutionOverlayBuilder.Build(
			furnaceSubgraph,
			analysis,
			PlayerCraftContextSnapshot.CreateSynthetic(Array.Empty<KeyValuePair<int, int>>()))
			.GetRecipeResolution(furnaceRecipe).State == RecipeLocalState.BlockedByStation,
			"The no-material/no-station Furnace regression was not BlockedByStation.");

		PlayerCraftContextSnapshot readyContext = PlayerCraftContextSnapshot.CreateSynthetic(
			new Dictionary<int, int> {
				[ItemID.StoneBlock] = 20,
				[ItemID.BorealWood] = 4,
				[ItemID.Torch] = 3
			},
			availableTileTypes: new[] { (int)TileID.WorkBenches });
		Assert(ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, readyContext)
			.GetRecipeResolution(furnaceRecipe).State == RecipeLocalState.ReadyNow,
			"A locally complete Furnace execution was not ReadyNow.");

		PlayerCraftContextSnapshot needsIngredientsContext = PlayerCraftContextSnapshot.CreateSynthetic(
			new Dictionary<int, int> {
				[ItemID.StoneBlock] = 8,
				[ItemID.BorealWood] = 4,
				[ItemID.Torch] = 3
			},
			availableTileTypes: new[] { (int)TileID.WorkBenches });
		Assert(ResolutionOverlayBuilder.Build(furnaceSubgraph, analysis, needsIngredientsContext)
			.GetRecipeResolution(furnaceRecipe).State == RecipeLocalState.NeedsIngredients,
			"An ingredient-deficient Furnace execution was not NeedsIngredients.");

		RecipeNode conditionRecipe = graph.RecipesByIndex.Values
			.Where(recipe => recipe.Constraints.HasConditions)
			.OrderBy(recipe => recipe.RecipeIndex)
			.First();
		TargetCraftSubgraph conditionSubgraph = TargetSubgraphExtractor.Extract(
			graph,
			analysis,
			conditionRecipe.Result,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 1, maximumGraphNodes: 250));
		Assert(conditionSubgraph.RecipeNodes.Contains(conditionRecipe), "The selected real condition recipe was not present in its target subgraph.");

		ResolutionSnapshot conditionSatisfiedOverlay = ResolutionOverlayBuilder.Build(
			conditionSubgraph,
			analysis,
			SyntheticConditions(conditionRecipe, CapturedConditionState.Satisfied));
		Assert(conditionSatisfiedOverlay.GetRecipeResolution(conditionRecipe).Conditions.All(condition =>
			condition.State == ConditionConstraintState.Satisfied),
			"The real condition recipe did not resolve captured true evidence as Satisfied.");

		ResolutionSnapshot conditionFalseOverlay = ResolutionOverlayBuilder.Build(
			conditionSubgraph,
			analysis,
			SyntheticConditions(conditionRecipe, CapturedConditionState.Unsatisfied));
		Assert(conditionFalseOverlay.GetRecipeResolution(conditionRecipe).State == RecipeLocalState.BlockedByCondition,
			"Scenario 5 was not BlockedByCondition.");

		ResolutionSnapshot conditionUnknownOverlay = ResolutionOverlayBuilder.Build(
			conditionSubgraph,
			analysis,
			SyntheticConditions(conditionRecipe, CapturedConditionState.Unknown));
		Assert(conditionUnknownOverlay.GetRecipeResolution(conditionRecipe).State == RecipeLocalState.UnknownConstraint,
			"Scenario 6 was not UnknownConstraint.");

		ValidateSharedInventoryBoundary(graph);
		ValidateCoverage(satisfiedOverlay);
		ValidateCoverage(ResolutionOverlayBuilder.Build(cellPhoneSubgraph, analysis, PlayerCraftContextSnapshot.CreateSynthetic(Array.Empty<KeyValuePair<int, int>>())));
		CraftGraphStatistics afterStatistics = graph.GetStatistics();
		Assert(beforeStatistics == afterStatistics, "Synthetic resolution changed CraftGraph statistics.");

		CraftResourceSourceSyntheticReport resourceSourceReport = CraftResourceSourceSyntheticTests.Run(graph, furnaceSubgraph);
		ResolutionSnapshot cellPhonePerformanceOverlay = ResolutionOverlayBuilder.Build(
			cellPhoneSubgraph,
			analysis,
			PlayerCraftContextSnapshot.CreateSynthetic(Array.Empty<KeyValuePair<int, int>>()));
		totalTimer.Stop();
		return new DynamicResolutionSyntheticReport(
			PassedScenarioCount: 6,
			conditionRecipe.RecipeIndex,
			conditionRecipe.Result.ItemType,
			conditionRecipe.Constraints.Conditions[0].Description,
			satisfiedContext.CaptureElapsed,
			satisfiedOverlay.Elapsed,
			cellPhonePerformanceOverlay.Elapsed,
			resourceSourceReport,
			totalTimer.Elapsed);
	}

	private static PlayerCraftContextSnapshot SyntheticInventory(params (int ItemType, int Count)[] items) =>
		PlayerCraftContextSnapshot.CreateSynthetic(items.Select(item =>
			new KeyValuePair<int, int>(item.ItemType, item.Count)));

	private static PlayerCraftContextSnapshot SyntheticConditions(
		RecipeNode recipe,
		CapturedConditionState state)
	{
		CapturedConditionEvidence[] evidence = recipe.Constraints.Conditions
			.Select(condition => new CapturedConditionEvidence(
				new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex),
				condition.Description,
				state,
				state == CapturedConditionState.Unknown ? "Synthetic unsupported condition." : null))
			.ToArray();
		return PlayerCraftContextSnapshot.CreateSynthetic(
			Array.Empty<KeyValuePair<int, int>>(),
			conditionEvidence: evidence);
	}

	private static void ValidateSharedInventoryBoundary(CraftGraph graph)
	{
		ItemNode wood = graph.ItemsByType[ItemID.Wood];
		RecipeNode[] recipes = graph.RecipesByIndex.Values.OrderBy(recipe => recipe.RecipeIndex).Take(2).ToArray();
		var emptyConstraints = new RecipeConstraintSet(Array.Empty<int>(), Array.Empty<RecipeConditionInfo>());
		var firstRecipe = new RecipeNode(-10001, recipes[0].Result, 1, emptyConstraints);
		var secondRecipe = new RecipeNode(-10002, recipes[1].Result, 1, emptyConstraints);
		var firstEdge = new IngredientEdge(0, wood, firstRecipe, 10, Array.Empty<RecipeGroupInfo>());
		var secondEdge = new IngredientEdge(0, wood, secondRecipe, 10, Array.Empty<RecipeGroupInfo>());
		PlayerCraftContextSnapshot context = SyntheticInventory((ItemID.Wood, 10));

		IngredientResolution first = ResolutionOverlayBuilder.ResolveIngredientRequirement(firstEdge, context);
		IngredientResolution second = ResolutionOverlayBuilder.ResolveIngredientRequirement(secondEdge, context);
		Assert(first.LocallySatisfied && second.LocallySatisfied, "Shared inventory edges were not independently locally satisfied.");
		Assert(first.AvailableAmount == 10 && second.AvailableAmount == 10, "Shared inventory evidence was unexpectedly allocated or consumed.");
		Assert(typeof(ResolutionSnapshot).GetProperty("GlobalPlanFeasible") is null &&
			typeof(ResolutionSnapshot).GetProperty("TargetRecursivelyCraftable") is null,
			"ResolutionSnapshot exposes a forbidden global planner conclusion.");
	}

	private static void ValidateCoverage(ResolutionSnapshot snapshot)
	{
		Assert(snapshot.ItemResolutions.Count == snapshot.SourceSubgraph.ItemNodes.Count, "Item resolution coverage mismatch.");
		Assert(snapshot.IngredientResolutions.Count == snapshot.SourceSubgraph.IngredientEdges.Count, "Ingredient resolution coverage mismatch.");
		Assert(snapshot.RecipeResolutions.Count == snapshot.SourceSubgraph.RecipeNodes.Count, "Recipe resolution coverage mismatch.");
	}

	private static void AssertIngredient(
		IngredientResolution resolution,
		int required,
		int available,
		bool locallySatisfied,
		string scenario)
	{
		Assert(resolution.RequiredAmount == required, $"{scenario} required amount mismatch.");
		Assert(resolution.AvailableAmount == available, $"{scenario} available amount mismatch.");
		Assert(resolution.LocallySatisfied == locallySatisfied, $"{scenario} local satisfaction mismatch.");
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"Dynamic resolution synthetic validation failed: {message}");
	}
}
