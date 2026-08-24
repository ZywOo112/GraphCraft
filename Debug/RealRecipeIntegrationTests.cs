using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;
using Terraria;
using Terraria.ID;

namespace GraphCraft.Debug;

internal sealed record RealRecipeIntegrationCase(
	string Category,
	int TargetItemType,
	IReadOnlyList<int> RootRecipeIndices,
	RecursivePlanStatus Status,
	int ItemNodeCount,
	int RecipeNodeCount,
	int TotalEdgeCount,
	int RecipeGroupExpandedItemCount,
	SubgraphTruncationReason TruncationReason,
	int PlannedStepCount,
	PlanningSearchStatistics SearchStatistics,
	TimeSpan ExtractionElapsed,
	TimeSpan PlanningElapsed,
	string Evidence);

internal sealed record RealRecipeIntegrationReport(
	IReadOnlyList<RealRecipeIntegrationCase> Cases,
	int PassedCaseCount,
	TimeSpan Elapsed);

internal static class RealRecipeIntegrationTests
{
	public static RealRecipeIntegrationReport Run(CraftGraph graph, GraphAnalysisResult analysis)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(analysis);
		var timer = Stopwatch.StartNew();
		var cases = new List<RealRecipeIntegrationCase>();

		RecipeNode simple = graph.RecipesByIndex.Values
			.Where(recipe => HasVanillaItemShape(recipe) &&
				recipe.Result.ProducerRecipes.Count == 1 && recipe.Ingredients.Count > 0 &&
				recipe.Ingredients.All(ingredient => !ingredient.AcceptsRecipeGroup && ingredient.Source.Classification == ItemClassification.Atomic) &&
				recipe.Constraints.Conditions.Count == 0)
			.OrderBy(recipe => recipe.Result.ItemType)
			.ThenBy(recipe => recipe.RecipeIndex)
			.FirstOrDefault() ?? throw new InvalidOperationException("No actual simple one-recipe target was available.");
		cases.Add(ValidateSuccessfulTarget("SimpleOneRecipe", simple.Result, graph, analysis, 2, 300, (subgraph, resources) =>
			SupplyDirectRecipe(simple, 1, resources), requireSteps: 1));

		ItemNode[] multiLevelCandidates = graph.ItemsByType.Values
			.Where(item => item.ItemType < ItemID.Count && item.ProducerRecipes.Count == 1 &&
				HasVanillaItemShape(item.ProducerRecipes[0]) &&
				LongestUniqueVanillaExactDependencyDepth(item, new HashSet<int>()) >= 3 &&
				RecursiveSupplyUsesOnlyVanillaItems(item.ProducerRecipes[0], 4, new HashSet<int>()))
			.OrderBy(item => item.ItemType)
			.ToArray();
		RealRecipeIntegrationCase? multiLevelCase = null;
		foreach (ItemNode candidate in multiLevelCandidates) {
			if (TryCreateSuccessfulTargetCase("MultiLevelRecursive", candidate, graph, analysis, 5, 1200, (subgraph, resources) =>
				SupplyRecursive(candidate.ProducerRecipes.Single(), 1, subgraph, resources, 4, new HashSet<int>()), requireSteps: 3,
				requestedAdditional: 1, resultCheck: null, out multiLevelCase))
				break;
		}
		cases.Add(multiLevelCase ?? throw new InvalidOperationException("No successful actual three-level vanilla-item target was available."));

		RecipeNode multiOutput = graph.RecipesByIndex.Values
			.Where(recipe => HasVanillaItemShape(recipe) &&
				recipe.OutputStack > 1 && recipe.Result.ProducerRecipes.Count == 1 && recipe.Ingredients.Count > 0 &&
				recipe.Ingredients.All(ingredient => !ingredient.AcceptsRecipeGroup && ingredient.Source.Classification == ItemClassification.Atomic) &&
				recipe.Constraints.Conditions.Count == 0)
			.OrderBy(recipe => recipe.Result.ItemType)
			.ThenBy(recipe => recipe.RecipeIndex)
			.FirstOrDefault() ?? throw new InvalidOperationException("No actual multi-output target was available.");
		int multiOutputRequest = multiOutput.OutputStack + 1;
		cases.Add(ValidateSuccessfulTarget("MultiOutput", multiOutput.Result, graph, analysis, 2, 300, (subgraph, resources) =>
			SupplyDirectRecipe(multiOutput, multiOutputRequest, resources), requireSteps: 1, requestedAdditional: multiOutputRequest,
			resultCheck: result => result.Steps.Last().CraftRepetitions == 2 && result.Steps.Last().ProducedQuantity == multiOutput.OutputStack * 2));

		RecipeNode groupRecipe = graph.RecipesByIndex.Values
			.Where(recipe => HasVanillaItemShape(recipe) &&
				recipe.Result.ProducerRecipes.Count == 1 && recipe.Ingredients.Any(ingredient =>
				ingredient.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().Count() >= 2) &&
				recipe.Constraints.Conditions.Count == 0)
			.OrderBy(recipe => recipe.Result.ItemType)
			.ThenBy(recipe => recipe.RecipeIndex)
			.FirstOrDefault() ?? throw new InvalidOperationException("No actual unique-root Recipe Group target was available.");
		IngredientEdge realGroupEdge = groupRecipe.Ingredients.First(ingredient =>
			ingredient.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().Count() >= 2);
		cases.Add(ValidateSuccessfulTarget("RealRecipeGroup", groupRecipe.Result, graph, analysis, 3, 900, (subgraph, resources) =>
			SupplyDirectRecipe(groupRecipe, 1, resources, splitGroup: true), requireSteps: 1,
			resultCheck: result => result.GroupAllocations.Any(allocation => ReferenceEquals(allocation.Requirement, realGroupEdge) &&
				allocation.ConcreteAllocations.Count >= 2)));

		ItemNode alternatives = graph.ItemsByType.Values
			.Where(item => item.ItemType < ItemID.Count && item.ProducerRecipes.Count == 2 &&
				item.ProducerRecipes.All(recipe => HasVanillaItemShape(recipe) &&
				recipe.Ingredients.Count > 0 && recipe.Constraints.Conditions.Count == 0 &&
				recipe.Ingredients.All(ingredient => !ingredient.AcceptsRecipeGroup && ingredient.Source.Classification == ItemClassification.Atomic)) &&
				!ConcreteRequirementTypes(item.ProducerRecipes[0]).Intersect(ConcreteRequirementTypes(item.ProducerRecipes[1])).Any())
			.OrderBy(item => item.ItemType)
			.FirstOrDefault() ?? throw new InvalidOperationException("No actual two-root direct target with isolated resources was available.");
		cases.Add(ValidateUniqueAlternativeTarget(alternatives, graph, analysis));
		cases.Add(ValidateAlternativeTarget(alternatives, graph, analysis));

		RecipeNode stationRecipe = graph.RecipesByIndex.Values
			.Where(recipe => HasVanillaItemShape(recipe) &&
				recipe.Result.ProducerRecipes.Count == 1 && recipe.Constraints.RequiredTileTypes.Count > 0 &&
				recipe.Ingredients.Count > 0 && recipe.Constraints.Conditions.Count == 0)
			.OrderByDescending(recipe => recipe.Constraints.RequiredTileTypes.Count)
			.ThenBy(recipe => recipe.Result.ItemType)
			.FirstOrDefault() ?? throw new InvalidOperationException("No actual unique-root station recipe was available.");
		cases.Add(ValidateStationBoundary(stationRecipe, graph, analysis));

		ItemNode longTarget = graph.ItemsByType.TryGetValue(ItemID.CellPhone, out ItemNode? cellPhone) &&
			cellPhone.ProducerRecipes.Count > 0 && cellPhone.ProducerRecipes.All(HasVanillaItemShape)
			? cellPhone
			: graph.ItemsByType.Values
				.Where(item => item.ItemType < ItemID.Count && item.ProducerRecipes.Count > 0 && item.ProducerRecipes.All(HasVanillaItemShape))
				.OrderByDescending(item => LongestUniqueVanillaExactDependencyDepth(item, new HashSet<int>()))
				.ThenBy(item => item.ItemType)
				.First();
		cases.Add(ValidateLongTarget(longTarget, graph, analysis));

		timer.Stop();
		return new RealRecipeIntegrationReport(new ReadOnlyCollection<RealRecipeIntegrationCase>(cases), cases.Count, timer.Elapsed);
	}

	private static RealRecipeIntegrationCase ValidateSuccessfulTarget(
		string category,
		ItemNode target,
		CraftGraph graph,
		GraphAnalysisResult analysis,
		int extractionDepth,
		int maximumNodes,
		Action<TargetCraftSubgraph, Dictionary<int, int>> supply,
		int requireSteps,
		int requestedAdditional = 1,
		Func<TargetCraftPlanResult, bool>? resultCheck = null)
	{
		if (TryCreateSuccessfulTargetCase(category, target, graph, analysis, extractionDepth, maximumNodes, supply,
			requireSteps, requestedAdditional, resultCheck, out RealRecipeIntegrationCase? integrationCase))
			return integrationCase!;
		throw new InvalidOperationException($"Actual Terraria integration case {category} failed its success assertions.");
	}

	private static bool TryCreateSuccessfulTargetCase(
		string category,
		ItemNode target,
		CraftGraph graph,
		GraphAnalysisResult analysis,
		int extractionDepth,
		int maximumNodes,
		Action<TargetCraftSubgraph, Dictionary<int, int>> supply,
		int requireSteps,
		int requestedAdditional,
		Func<TargetCraftPlanResult, bool>? resultCheck,
		out RealRecipeIntegrationCase? integrationCase)
	{
		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(graph, analysis, target,
			TargetSubgraphOptions.Bounded(extractionDepth, maximumNodes));
		var resources = new Dictionary<int, int>();
		supply(subgraph, resources);
		TargetCraftPlanResult result = Plan(subgraph, analysis, resources, requestedAdditional, includeStations: true, out TimeSpan planningElapsed);
		if (!result.Succeeded || result.Steps.Count < requireSteps || (resultCheck is not null && !resultCheck(result))) {
			integrationCase = null;
			return false;
		}
		integrationCase = Case(category, subgraph, result, planningElapsed,
			$"Terraria-owned canonical recipe metadata; resources={FormatResources(resources)}; roots={FormatRoots(subgraph)}");
		return true;
	}

	private static RealRecipeIntegrationCase ValidateAlternativeTarget(ItemNode target, CraftGraph graph, GraphAnalysisResult analysis)
	{
		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(graph, analysis, target, TargetSubgraphOptions.Bounded(2, 700));
		RecipeNode[] roots = target.ProducerRecipes.Where(subgraph.RecipeNodes.Contains).OrderBy(recipe => recipe.RecipeIndex).Take(2).ToArray();
		var resources = new Dictionary<int, int>();
		foreach (RecipeNode root in roots)
			SupplyDirectRecipe(root, 1, resources);
		TargetCraftPlanResult result = Plan(subgraph, analysis, resources, 1, includeStations: true, out TimeSpan elapsed);
		RootRecipeDecision? decision = result.RootDecision;
		if (result.Status != RecursivePlanStatus.UserChoiceRequired || decision is null || decision.UserSelectableCandidates.Count < 2)
			throw new InvalidOperationException($"Actual multiple-root integration did not expose two complete routes: {result.Status}.");
		return Case("MultipleRootRecipes", subgraph, result, elapsed,
			$"confirmed root-success candidates={decision.UserSelectableCandidates.Count}; resources={FormatResources(resources)}; roots={FormatRoots(subgraph)}");
	}

	private static RealRecipeIntegrationCase ValidateUniqueAlternativeTarget(ItemNode target, CraftGraph graph, GraphAnalysisResult analysis)
	{
		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(graph, analysis, target, TargetSubgraphOptions.Bounded(2, 500));
		RecipeNode selected = target.ProducerRecipes.OrderBy(recipe => recipe.RecipeIndex).First();
		var resources = new Dictionary<int, int>();
		SupplyDirectRecipe(selected, 1, resources);
		TargetCraftPlanResult result = Plan(subgraph, analysis, resources, 1, includeStations: true, out TimeSpan elapsed);
		if (!result.Succeeded || result.SelectedRootRecipe != selected || result.RootDecision?.FullyViableCandidateCount != 1)
			throw new InvalidOperationException($"Actual multiple-root unique-route integration mismatch: {result.Status}.");
		return Case("MultipleRootUniqueViable", subgraph, result, elapsed,
			$"auto-selected root={selected.RecipeIndex}; other canonical root unavailable; resources={FormatResources(resources)}; roots={FormatRoots(subgraph)}");
	}

	private static RealRecipeIntegrationCase ValidateStationBoundary(RecipeNode recipe, CraftGraph graph, GraphAnalysisResult analysis)
	{
		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(graph, analysis, recipe.Result, TargetSubgraphOptions.Bounded(2, 500));
		var resources = new Dictionary<int, int>();
		SupplyDirectRecipe(recipe, 1, resources);
		TargetCraftPlanResult blocked = Plan(subgraph, analysis, resources, 1, includeStations: false, out _);
		TargetCraftPlanResult ready = Plan(subgraph, analysis, resources, 1, includeStations: true, out TimeSpan elapsed);
		if (blocked.Status != RecursivePlanStatus.BlockedByStation || !ready.Succeeded)
			throw new InvalidOperationException($"Actual station boundary mismatch: blocked={blocked.Status}, ready={ready.Status}.");
		return Case("StationBoundary", subgraph, ready, elapsed,
			$"without station={blocked.Status}; resources={FormatResources(resources)}; roots={FormatRoots(subgraph)}");
	}

	private static RealRecipeIntegrationCase ValidateLongTarget(ItemNode target, CraftGraph graph, GraphAnalysisResult analysis)
	{
		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(graph, analysis, target, TargetSubgraphOptions.Bounded(6, 2500));
		var resources = new Dictionary<int, int>();
		foreach (RecipeNode root in target.ProducerRecipes.Where(subgraph.RecipeNodes.Contains).OrderBy(recipe => recipe.RecipeIndex).Take(1))
			SupplyRecursive(root, 1, subgraph, resources, 6, new HashSet<int>());
		TargetCraftPlanResult result = Plan(subgraph, analysis, resources, 1, includeStations: true, out TimeSpan elapsed);
		if (result.Status == RecursivePlanStatus.SearchBudgetBoundary || result.SearchStatistics.ExpandedBranches >= result.SearchStatistics.MaximumBranchExpansions)
			throw new InvalidOperationException("Representative long actual target exhausted the default search budget.");
		return Case("LongTargetGraph", subgraph, result, elapsed,
			$"bounded actual graph stress; no budget exhaustion; resources={FormatResources(resources)}; roots={FormatRoots(subgraph)}");
	}

	private static TargetCraftPlanResult Plan(TargetCraftSubgraph subgraph, GraphAnalysisResult analysis,
		IReadOnlyDictionary<int, int> resources, int requestedAdditional, bool includeStations, out TimeSpan elapsed)
	{
		int[] tiles = includeStations
			? subgraph.RecipeNodes.SelectMany(recipe => recipe.Constraints.RequiredTileTypes).Distinct().OrderBy(tile => tile).ToArray()
			: Array.Empty<int>();
		CapturedConditionEvidence[] conditions = subgraph.RecipeNodes
			.SelectMany(recipe => recipe.Constraints.Conditions.Select(condition => new CapturedConditionEvidence(
				new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex), condition.Description, CapturedConditionState.Satisfied)))
			.ToArray();
		PlayerCraftContextSnapshot context = PlayerCraftContextSnapshot.CreateSynthetic(resources, tiles, conditions);
		ResolutionSnapshot resolution = ResolutionOverlayBuilder.Build(subgraph, analysis, context);
		var timer = Stopwatch.StartNew();
		TargetCraftPlanResult result = TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			subgraph, resolution, subgraph.Target, requestedAdditional, VirtualResourceLedger.Create(context.Resources)));
		timer.Stop();
		elapsed = timer.Elapsed;
		return result;
	}

	private static void SupplyDirectRecipe(RecipeNode recipe, long requestedOutput, Dictionary<int, int> resources, bool splitGroup = false)
	{
		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutput);
		foreach (IngredientEdge ingredient in recipe.Ingredients) {
			long required = quantities.ScaleIngredient(ingredient);
			if (!ingredient.AcceptsRecipeGroup) {
				Add(resources, ingredient.Source.ItemType, required);
				continue;
			}
			int[] accepted = ingredient.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().OrderBy(item => item).ToArray();
			if (splitGroup && accepted.Length >= 2 && required >= 2) {
				long first = required / 2;
				Add(resources, accepted[0], first);
				Add(resources, accepted[1], required - first);
			}
			else {
				Add(resources, accepted[0], required);
			}
		}
	}

	private static void SupplyRecursive(RecipeNode recipe, long requestedOutput, TargetCraftSubgraph subgraph,
		Dictionary<int, int> resources, int remainingDepth, HashSet<int> activeItems)
	{
		RecipePlanningQuantities quantities = RecipePlanningMath.Calculate(recipe, requestedOutput);
		foreach (IngredientEdge ingredient in recipe.Ingredients) {
			long required = quantities.ScaleIngredient(ingredient);
			if (ingredient.AcceptsRecipeGroup) {
				int item = ingredient.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().OrderBy(value => value).First();
				Add(resources, item, required);
				continue;
			}
			RecipeNode[] producers = ingredient.Source.ProducerRecipes.Where(subgraph.RecipeNodes.Contains).OrderBy(value => value.RecipeIndex).ToArray();
			if (remainingDepth > 0 && producers.Length == 1 && activeItems.Add(ingredient.Source.ItemType)) {
				SupplyRecursive(producers[0], required, subgraph, resources, remainingDepth - 1, activeItems);
				activeItems.Remove(ingredient.Source.ItemType);
			}
			else {
				Add(resources, ingredient.Source.ItemType, required);
			}
		}
	}

	private static int LongestUniqueVanillaExactDependencyDepth(ItemNode item, HashSet<int> active)
	{
		if (!active.Add(item.ItemType))
			return 0;
		try {
			if (item.ProducerRecipes.Count != 1 || !HasVanillaItemShape(item.ProducerRecipes[0]))
				return 0;
			RecipeNode recipe = item.ProducerRecipes[0];
			return 1 + recipe.Ingredients.Where(ingredient => !ingredient.AcceptsRecipeGroup)
				.Select(ingredient => LongestUniqueVanillaExactDependencyDepth(ingredient.Source, active))
				.DefaultIfEmpty(0)
				.Max();
		}
		finally {
			active.Remove(item.ItemType);
		}
	}

	private static bool IsVanillaRecipe(RecipeNode recipe) =>
		recipe.RecipeIndex >= 0 && recipe.RecipeIndex < Main.recipe.Length && Main.recipe[recipe.RecipeIndex].Mod is null;

	private static bool HasVanillaItemShape(RecipeNode recipe) =>
		IsVanillaRecipe(recipe) && recipe.Result.ItemType < ItemID.Count &&
		recipe.Ingredients.All(ingredient => ingredient.Source.ItemType < ItemID.Count);

	private static bool RecursiveSupplyUsesOnlyVanillaItems(RecipeNode recipe, int remainingDepth, HashSet<int> activeItems)
	{
		if (!HasVanillaItemShape(recipe))
			return false;
		foreach (IngredientEdge ingredient in recipe.Ingredients.Where(ingredient => !ingredient.AcceptsRecipeGroup)) {
			if (ingredient.Source.ItemType >= ItemID.Count)
				return false;
			RecipeNode[] producers = ingredient.Source.ProducerRecipes.OrderBy(value => value.RecipeIndex).ToArray();
			if (remainingDepth > 0 && producers.Length == 1 && activeItems.Add(ingredient.Source.ItemType)) {
				bool valid = RecursiveSupplyUsesOnlyVanillaItems(producers[0], remainingDepth - 1, activeItems);
				activeItems.Remove(ingredient.Source.ItemType);
				if (!valid)
					return false;
			}
		}
		return true;
	}

	private static string FormatResources(IReadOnlyDictionary<int, int> resources) => string.Join(",",
		resources.OrderBy(pair => pair.Key).Select(pair => $"{Lang.GetItemNameValue(pair.Key)}({pair.Key})x{pair.Value}"));

	private static string FormatRoots(TargetCraftSubgraph subgraph) => string.Join(" | ",
		subgraph.Target.ProducerRecipes.Where(subgraph.RecipeNodes.Contains).OrderBy(recipe => recipe.RecipeIndex).Select(recipe =>
			$"#{recipe.RecipeIndex} output={recipe.OutputStack} ingredients=[{string.Join(",", recipe.Ingredients.Select(ingredient =>
				$"{Lang.GetItemNameValue(ingredient.Source.ItemType)}({ingredient.Source.ItemType})x{ingredient.RequiredAmount}" +
				(ingredient.AcceptsRecipeGroup ? $"{{groups:{string.Join(',', ingredient.AcceptedRecipeGroups.Select(group => group.GroupId))}}}" : string.Empty)))}] " +
			$"tiles=[{string.Join(',', recipe.Constraints.RequiredTileTypes)}] conditions={recipe.Constraints.Conditions.Count}"));

	private static void Add(Dictionary<int, int> resources, int itemType, long quantity)
	{
		int value = checked((int)quantity);
		resources[itemType] = checked(resources.GetValueOrDefault(itemType) + value);
	}

	private static IReadOnlyList<int> ConcreteRequirementTypes(RecipeNode recipe) => recipe.Ingredients
		.Select(ingredient => ingredient.Source.ItemType)
		.Distinct()
		.OrderBy(itemType => itemType)
		.ToArray();

	private static RealRecipeIntegrationCase Case(string category, TargetCraftSubgraph subgraph,
		TargetCraftPlanResult result, TimeSpan planningElapsed, string evidence) => new(
		category,
		subgraph.Target.ItemType,
		subgraph.Target.ProducerRecipes.Where(subgraph.RecipeNodes.Contains).OrderBy(recipe => recipe.RecipeIndex).Select(recipe => recipe.RecipeIndex).ToArray(),
		result.Status,
		subgraph.ItemNodes.Count,
		subgraph.RecipeNodes.Count,
		subgraph.TotalEdgeCount,
		subgraph.RecipeGroupExpandedItemCount,
		subgraph.TruncationReason,
		result.Steps.Count,
		result.SearchStatistics,
		subgraph.Elapsed,
		planningElapsed,
		evidence);
}
