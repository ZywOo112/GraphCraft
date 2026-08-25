using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Systems.Execution;
using Terraria.ID;

namespace GraphCraft.Debug;

internal sealed record TransactionalRealRecipeCase(
	string Name,
	int ResultItemType,
	int RecipeIndex,
	int PreparedCraftCount,
	long MaximumIngredientQuantity,
	long ActualIngredientQuantity,
	long OutputQuantity);

internal sealed record TransactionalRealRecipeIntegrationReport(
	IReadOnlyList<TransactionalRealRecipeCase> Cases,
	int PassedCaseCount,
	TimeSpan Elapsed);

internal static class TransactionalRealRecipeIntegrationTests
{
	public static TransactionalRealRecipeIntegrationReport Run(CraftGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		var timer = Stopwatch.StartNew();
		var cases = new List<TransactionalRealRecipeCase>();
		Validate("Copper Watch", RecipeFor(graph, ItemID.CopperWatch), cases);
		Validate("Glass -> Bottle", RecipeFor(graph, ItemID.Bottle, ItemID.Glass), cases);
		Validate("Bottle -> Lesser Healing Potion", RecipeFor(graph, ItemID.LesserHealingPotion, ItemID.Bottle), cases);
		Validate("Lesser -> Healing Potion", RecipeFor(graph, ItemID.HealingPotion, ItemID.LesserHealingPotion), cases);
		Validate("Iron Pickaxe Recipe Group", RecipeFor(graph, ItemID.IronPickaxe), cases);
		timer.Stop();
		return new TransactionalRealRecipeIntegrationReport(
			new ReadOnlyCollection<TransactionalRealRecipeCase>(cases),
			cases.Count,
			timer.Elapsed);
	}

	private static RecipeNode RecipeFor(CraftGraph graph, int resultItemType, int? requiredInput = null)
	{
		if (!graph.TryGetItemNode(resultItemType, out ItemNode? item) || item is null)
			throw new InvalidOperationException($"Actual M5.2 target ItemType {resultItemType} is absent from CraftGraph.");
		return item.ProducerRecipes
			.Where(recipe => requiredInput is null || recipe.Ingredients.Any(edge => edge.Source.ItemType == requiredInput))
			.OrderBy(recipe => recipe.RecipeIndex)
			.FirstOrDefault()
			?? throw new InvalidOperationException(
				$"Actual M5.2 recipe for ItemType {resultItemType} with input {requiredInput?.ToString() ?? "any"} is absent.");
	}

	private static void Validate(
		string name,
		RecipeNode recipe,
		ICollection<TransactionalRealRecipeCase> cases)
	{
		PlannedIngredientUse[] uses = recipe.Ingredients
			.OrderBy(edge => edge.Ordinal)
			.Select(edge => new PlannedIngredientUse(
				edge,
				edge.RequiredAmount,
				edge.RequiredAmount,
				0,
				edge.RequiredAmount,
				0))
			.ToArray();
		var logical = new PlannedCraftStep(
			0,
			0,
			recipe,
			recipe.OutputStack,
			1,
			recipe.OutputStack,
			uses);
		ExecutionResourceDemand[] demands = recipe.Ingredients
			.OrderBy(edge => edge.Ordinal)
			.Select(edge => new ExecutionResourceDemand(
				0,
				edge.Ordinal,
				edge.Source.ItemType,
				edge.RequiredAmount,
				0,
				edge.RequiredAmount,
				edge.AcceptsRecipeGroup))
			.ToArray();
		var step = new ExecutionStepPlan(
			logical,
			demands,
			new ExecutionProduction(recipe.Result.ItemType, recipe.OutputStack, ExecutionProductionKind.FinalOutput));
		var semantics = new TerrariaRecipeExecutionSemantics();
		CraftPreparedRecipeStep prepared = semantics.PrepareStep(step);
		long maximum = demands.Sum(demand => demand.RequiredQuantity);
		long actual = prepared.Crafts.SelectMany(craft => craft.ActualIngredients).Sum(item => item.Quantity);
		long output = prepared.Crafts.SelectMany(craft => craft.Outputs).Sum(item => item.Quantity);
		if (prepared.Crafts.Count != 1 || actual < 0 || actual > maximum || output != recipe.OutputStack ||
			prepared.Crafts.SelectMany(craft => craft.Outputs).Any(item => item.ItemType != recipe.Result.ItemType)) {
			throw new InvalidOperationException($"Actual Terraria M5.2 preparation failed for {name} Recipe #{recipe.RecipeIndex}.");
		}
		cases.Add(new TransactionalRealRecipeCase(
			name,
			recipe.Result.ItemType,
			recipe.RecipeIndex,
			prepared.Crafts.Count,
			maximum,
			actual,
			output));
	}
}
