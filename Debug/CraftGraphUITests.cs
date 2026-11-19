using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;
using GraphCraft.UI;
using Terraria.ID;

namespace GraphCraft.Debug;

internal static class CraftGraphUITests
{
	public static IReadOnlyList<string> Run(CraftGraph actualGraph, GraphAnalysisResult actualAnalysis, bool vanillaRecipeFixtures)
	{
		var passes = new List<string>();
		Synthetic(passes);
		if (vanillaRecipeFixtures)
			Actual(actualGraph, actualAnalysis, passes);
		return passes;
	}

	private static void Synthetic(List<string> passes)
	{
		var graph = new CraftGraph();
		RecipeNode first = AddRecipe(graph, 0, 100, 101, 103);
		RecipeNode second = AddRecipe(graph, 1, 100, 102, 103);
		AddRecipe(graph, 2, 101, 104);
		AddRecipe(graph, 3, 102, 105);
		AddRecipe(graph, 4, 110, 111);
		AddRecipe(graph, 5, 111, 110);
		var condition = new RecipeConditionInfo(0, "GraphCraft.Test", "Test condition", null);
		RecipeNode constrained = graph.AddRecipe(6, 120, 1, new RecipeConstraintSet([41], [condition]));
		graph.AddIngredient(constrained, 121, 1, []);
		graph.Seal();
		GraphAnalysisResult analysis = GraphAnalyzer.Analyze(graph);
		CraftGraphView view = Create(graph, analysis, 100);
		GraphViewLayout initial = view.Layout();
		var shared = new GraphNodeKey(CraftNodeKind.Item, 103);
		Require(initial.Nodes.Count == 6 && initial.Links.Count == 6 &&
			initial.Nodes.Count(node => node.Key.NodeKind == CraftNodeKind.Recipe) == 2 &&
			initial.Nodes.Count(node => node.Key.NodeKind == CraftNodeKind.Item) == 4,
			"Item and Recipe projection", passes);
		Require(initial.Nodes.Count(node => node.Key == shared) == 1 &&
			initial.Links.Count(link => link.Dependency == shared) == 2 && view.IsShared(shared),
			"Shared dependency identity", passes);
		view.ToggleExpansion(GraphNodeKey.From(first));
		GraphViewLayout collapsed = view.Layout();
		Require(collapsed.Nodes.All(node => node.Key.LocalId != 101) &&
			collapsed.Links.Count(link => link.Dependency == shared) == 1 &&
			collapsed.Nodes.Any(node => node.Key == shared),
			"Local collapse preserves a shared branch", passes);
		view.ToggleExpansion(GraphNodeKey.From(first));
		Require(initial.Nodes.SequenceEqual(view.Layout().Nodes) && initial.Links.SequenceEqual(view.Layout().Links) &&
			initial.Nodes.Select(node => (node.Column, node.Row)).Distinct().Count() == initial.Nodes.Count,
			"Deterministic non-overlapping layout", passes);

		var focus = new GraphNodeKey(CraftNodeKind.Item, 101);
		view.Select(focus);
		view.FocusSelection();
		bool focused = view.Root.LocalId == 100 && view.Focus == focus && view.Layout().Nodes[0].Key == focus &&
			view.Layout().Nodes.Any(node => node.Key.LocalId == 2 && node.Key.NodeKind == CraftNodeKind.Recipe) &&
			view.Layout().Nodes.All(node => node.Key.LocalId != 102);
		view.Select(new GraphNodeKey(CraftNodeKind.Recipe, 2));
		view.FocusSelection();
		view.Back();
		bool backed = view.Focus == focus && view.Selection == focus;
		view.ReturnToRoot();
		Require(focused && backed && view.Focus == view.Root && view.Selection == view.Root && !view.CanGoBack,
			"Focus, back history and stable root", passes);

		GraphViewLayout beforeRefresh = view.Layout();
		var stock = new Dictionary<int, int> { [101] = 1, [103] = 1 };
		view.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(stock), 1);
		GraphViewEvidence before = view.Evidence;
		Require(before.Route == GraphRouteState.Craftable &&
			before.Plan.RootDecision?.State == RootRecipeDecisionState.AutoSelectedOnlyViable &&
			before.RootCandidate(first)?.Status == AlternativeCandidateStatus.FullyViable &&
			before.RootCandidate(second)?.Status != AlternativeCandidateStatus.FullyViable,
			"Single complete alternative", passes);
		stock[102] = 1;
		var richer = PlayerCraftContextSnapshot.CreateSynthetic(stock);
		view.Refresh(richer, 1);
		Require(view.Evidence.Route == GraphRouteState.ChoiceRequired &&
			view.Evidence.Plan.RootDecision?.FullyViableCandidateCount == 2 &&
			!view.Evidence.IsPlanned(first) && !view.Evidence.IsPlanned(second),
			"Multiple complete alternatives require a choice", passes);
		view.Refresh(richer, 2);
		Require(view.Evidence.Route == GraphRouteState.NoRoute &&
			view.Evidence.Plan.Status == RecursivePlanStatus.NoViableAlternative &&
			view.Evidence.Resolution.GetRecipeResolution(first).State == RecipeLocalState.ReadyNow,
			"ReadyNow remains distinct from target feasibility", passes);
		Require(ReferenceEquals(view.Evidence.Resolution.Context, richer) &&
			view.Evidence.Resolution.ResolutionId != before.Resolution.ResolutionId &&
			view.Evidence.Plan.RequestedAdditionalOutputQuantity == 2 &&
			before.Resolution.Context.GetAvailableCount(102) == 0 &&
			beforeRefresh.Nodes.SequenceEqual(view.Layout().Nodes) && beforeRefresh.Links.SequenceEqual(view.Layout().Links),
			"Refresh replaces snapshot and plan without changing layout or prior evidence", passes);

		GraphViewLayout bounded = view.Layout(nodeLimit: 3, linkLimit: 2);
		Require(bounded.Nodes.Count == 3 && bounded.Links.Count == 2 && bounded.HitDisplayLimit,
			"Visible node and connection caps", passes);
		CraftGraphView truncated = Create(graph, analysis, 100, depth: 0);
		truncated.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(stock), 1);
		Require(truncated.Subgraph.IsTruncated && truncated.Layout().Nodes.Count == 1 &&
			truncated.Evidence.Route == GraphRouteState.Deferred,
			"Truncation is a planning boundary", passes);

		CraftGraphView cycle = Create(graph, analysis, 110);
		foreach (ICraftGraphNode node in cycle.Subgraph.ItemNodes.Cast<ICraftGraphNode>().Concat(cycle.Subgraph.RecipeNodes)) {
			GraphNodeKey key = GraphNodeKey.From(node);
			if (!cycle.IsExpanded(key))
				cycle.ToggleExpansion(key);
		}
		GraphViewLayout cyclic = cycle.Layout();
		Require(cyclic.Nodes.Count == 4 && cyclic.Links.Count == 4 && cyclic.Links.All(link => link.InCycle) &&
			cycle.IsCyclic(cycle.Root), "Cycles retain closing links and terminate layout", passes);

		CraftGraphView constraints = Create(graph, analysis, 120);
		var materials = new Dictionary<int, int> { [121] = 1 };
		constraints.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(materials, conditionEvidence: [
			new CapturedConditionEvidence(new RecipeConditionKey(6, 0), condition.Description, CapturedConditionState.Satisfied)
		]), 1);
		bool blockedByStation = constraints.Evidence.Route == GraphRouteState.BlockedByStation &&
			constraints.Evidence.Resolution.GetRecipeResolution(constrained).State == RecipeLocalState.BlockedByStation;
		constraints.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(materials, [41]), 1);
		bool unknown = constraints.Evidence.Route == GraphRouteState.UnknownConstraint &&
			constraints.Evidence.Resolution.GetRecipeResolution(constrained).State == RecipeLocalState.UnknownConstraint;
		constraints.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(materials, [41], [
			new CapturedConditionEvidence(new RecipeConditionKey(6, 0), condition.Description, CapturedConditionState.Unsatisfied)
		]), 1);
		Require(blockedByStation && unknown && constraints.Evidence.Route == GraphRouteState.BlockedByCondition &&
			constraints.Evidence.Resolution.GetRecipeResolution(constrained).State == RecipeLocalState.BlockedByCondition &&
			GraphViewEvidence.Classify(RecursivePlanStatus.NoViablePlan) == GraphRouteState.NoRoute,
			"Station, condition, unknown and unavailable states", passes);
	}

	private static void Actual(CraftGraph graph, GraphAnalysisResult analysis, List<string> passes)
	{
		CraftGraphView watch = Create(graph, analysis, ItemID.CopperWatch);
		RefreshActual(watch, (ItemID.CopperBar, 10), (ItemID.Chain, 1));
		RecipeNode watchRecipe = watch.Evidence.Plan.SelectedRootRecipe!;
		Require(watch.Evidence.Route == GraphRouteState.Craftable && watch.Evidence.Resolution.GetRecipeResolution(watchRecipe).State ==
			RecipeLocalState.ReadyNow && watch.Layout().Links.Count(link => link.Ingredient is null) >= 1 &&
			watch.Layout().Links.Any(link => link.Ingredient?.Source.ItemType == ItemID.CopperBar),
			"Terraria Copper Watch direct graph", passes);

		CraftGraphView potion = Create(graph, analysis, ItemID.HealingPotion);
		RefreshActual(potion, (ItemID.Glass, 10), (ItemID.Mushroom, 10), (ItemID.Gel, 10), (ItemID.GlowingMushroom, 10));
		RecipeNode potionRecipe = potion.Evidence.Plan.SelectedRootRecipe!;
		Require(potion.Evidence.Route == GraphRouteState.Craftable &&
			potion.Evidence.Resolution.GetRecipeResolution(potionRecipe).State == RecipeLocalState.NeedsIngredients &&
			potion.Evidence.Plan.Steps.Select(step => step.Recipe.Result.ItemType)
				.SequenceEqual([ItemID.Bottle, ItemID.LesserHealingPotion, ItemID.HealingPotion]),
			"Terraria Healing Potion recursive plan with local deficit", passes);

		CraftGraphView pickaxe = Create(graph, analysis, ItemID.IronPickaxe);
		RefreshActual(pickaxe, (ItemID.IronBar, 10), (ItemID.Wood, 1), (ItemID.BorealWood, 2));
		RecipeGroupAllocation group = pickaxe.Evidence.Plan.GroupAllocations.Single();
		GraphViewLink[] members = pickaxe.Outgoing(GraphNodeKey.From(group.Requirement.Target))
			.Where(link => ReferenceEquals(link.Ingredient, group.Requirement)).ToArray();
		int[] highlighted = members.Where(link => pickaxe.Evidence.IsPlanned(link, pickaxe))
			.Select(link => link.Dependency.LocalId).ToArray();
		Require(pickaxe.Evidence.Route == GraphRouteState.Craftable && members.Length > highlighted.Length &&
			highlighted.SequenceEqual(new int[] { ItemID.Wood, ItemID.BorealWood }.Order()) &&
			group.ConcreteAllocations.Single(use => use.ItemType == ItemID.BorealWood).Quantity == 2,
			"Terraria Iron Pickaxe logical group and concrete allocation", passes);

		CraftGraphView battle = Create(graph, analysis, ItemID.BattlePotion);
		RecipeNode[] alternatives = battle.Outgoing(battle.Root).Select(link => (RecipeNode)battle.Node(link.Dependency)).ToArray();
		RefreshActual(battle, alternatives.SelectMany(recipe => recipe.Ingredients)
			.GroupBy(edge => edge.Source.ItemType).Select(grouping =>
				(grouping.Key, grouping.Sum(edge => edge.RequiredAmount))).ToArray());
		Require(alternatives.Length >= 2 && battle.Evidence.Route == GraphRouteState.ChoiceRequired &&
			alternatives.All(recipe => battle.Evidence.RootCandidate(recipe)?.Status == AlternativeCandidateStatus.FullyViable),
			"Terraria Battle Potion alternative routes", passes);

		CraftGraphView phone = Create(graph, analysis, ItemID.CellPhone, maximumNodes: 180);
		RefreshActual(phone);
		var tech = new GraphNodeKey(CraftNodeKind.Item, ItemID.GoblinTech);
		foreach (ICraftGraphNode node in phone.Subgraph.ItemNodes.Cast<ICraftGraphNode>().Concat(phone.Subgraph.RecipeNodes)) {
			GraphNodeKey key = GraphNodeKey.From(node);
			if (!phone.IsExpanded(key))
				phone.ToggleExpansion(key);
		}
		GraphViewLayout phoneLayout = phone.Layout(nodeLimit: 12, linkLimit: 100);
		phone.Select(tech);
		phone.FocusSelection();
		bool techFocused = phone.Focus == tech && phone.Layout().Nodes[0].Key == tech;
		phone.Back();
		Require(phone.Subgraph.TotalNodeCount <= 180 && phoneLayout.Nodes.Count <= 12 &&
			phoneLayout.HitDisplayLimit && techFocused && phone.Root.LocalId == ItemID.CellPhone && phone.Focus == phone.Root,
			"Terraria Cell Phone / Goblin Tech navigation and display budget", passes);
	}

	private static void RefreshActual(CraftGraphView view, params (int Type, int Count)[] stock)
	{
		int[] stations = view.Subgraph.RecipeNodes.SelectMany(recipe => recipe.Constraints.RequiredTileTypes).Distinct().ToArray();
		var conditions = view.Subgraph.RecipeNodes.SelectMany(recipe => recipe.Constraints.Conditions.Select(condition =>
			new CapturedConditionEvidence(new RecipeConditionKey(recipe.RecipeIndex, condition.ConditionIndex),
				condition.Description, CapturedConditionState.Satisfied)));
		view.Refresh(PlayerCraftContextSnapshot.CreateSynthetic(
			stock.ToDictionary(entry => entry.Type, entry => entry.Count), stations, conditions), 1);
	}

	private static CraftGraphView Create(CraftGraph graph, GraphAnalysisResult analysis, int itemType, int depth = 10, int maximumNodes = 1200) =>
		new(TargetSubgraphExtractor.Extract(graph, analysis, graph.ItemsByType[itemType],
			TargetSubgraphOptions.Bounded(depth, maximumNodes)), analysis);

	private static RecipeNode AddRecipe(CraftGraph graph, int recipeIndex, int output, params int[] inputs)
	{
		RecipeNode recipe = graph.AddRecipe(recipeIndex, output, 1, new RecipeConstraintSet([], []));
		foreach (int input in inputs)
			graph.AddIngredient(recipe, input, 1, []);
		return recipe;
	}

	private static void Require(bool passes, string name, List<string> results)
	{
		if (!passes)
			throw new InvalidOperationException($"M6.1 UI validation failed: {name}.");
		results.Add(name);
	}
}
