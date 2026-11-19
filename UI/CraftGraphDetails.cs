using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.Map;
using Terraria.UI;
using static GraphCraft.UI.CraftGraphUISystem;
using static GraphCraft.UI.CraftGraphWindow;

namespace GraphCraft.UI;

internal static class CraftGraphDetails
{
	private const int LineLimit = 32;
	private static readonly Color Section = new(133, 209, 192);
	private static readonly Color Muted = new(177, 191, 213);

	internal static string NodeName(ICraftGraphNode node) => node is ItemNode item
		? Lang.GetItemNameValue(item.ItemType)
		: Text("RecipeName", ((RecipeNode)node).RecipeIndex);

	public static IEnumerable<UIElement> Build(CraftGraphView view)
	{
		GraphNodeKey key = view.Selection;
		ICraftGraphNode node = view.Node(key);
		int itemType = node is ItemNode item ? item.ItemType : ((RecipeNode)node).Result.ItemType;
		yield return new DetailTitle(NodeName(node), itemType);
		if (view.IsCyclic(key))
			yield return Line(Text("Cycle"), Muted);
		if (view.IsShared(key))
			yield return Line(Text("SharedEvidence"), Muted);

		foreach (UIElement line in node is ItemNode selectedItem ? ItemDetails(view, selectedItem) : RecipeDetails(view, (RecipeNode)node))
			yield return line;
	}

	private static IEnumerable<UIElement> ItemDetails(CraftGraphView view, ItemNode item)
	{
		GraphViewEvidence evidence = view.Evidence;
		GraphNodeKey key = GraphNodeKey.From(item);
		yield return Line(Text("ItemCount", item.ItemType, evidence.Resolution.GetItemResolution(item).CurrentCount));
		if (key == view.Root) {
			yield return Line(Text("Requested", evidence.Plan.RequestedAdditionalOutputQuantity));
			if (evidence.Plan.RootDecision is { } decision)
				yield return Line(Text("Decision." + decision.State, decision.FullyViableCandidateCount), Section);
			if (evidence.Plan.Frontier?.DependencyFrontier is { } frontier) {
				yield return Line(Text("Frontier", Lang.GetItemNameValue(frontier.Item.ItemType),
					Text("Plan." + frontier.Kind)));
				if (frontier.RequiredQuantity > 0 && frontier.DeficitQuantity > 0)
					yield return Line(Text("LedgerAmounts", frontier.RequiredQuantity, frontier.AvailableQuantity,
						frontier.DeficitQuantity), Muted);
			}
			if (evidence.Plan.PendingRecipeDecision is { } pending)
				yield return Line(Text("PendingChoice", Lang.GetItemNameValue(pending.Item.ItemType), pending.FullyViableCandidateCount));
			if (evidence.Plan.SearchStatistics.BudgetExhausted)
				yield return Line(Text("PlannerBudget"), Muted);
		}

		int producerCount = view.Outgoing(key).Count;
		yield return Line(Text("Producers", producerCount, item.ProducerRecipes.Count), Section);
		foreach (GraphViewLink link in view.Outgoing(key).Take(LineLimit)) {
			var recipe = (RecipeNode)view.Node(link.Dependency);
			string local = Text("Local." + evidence.Resolution.GetRecipeResolution(recipe).State);
			string name = Text("RecipeCard", recipe.RecipeIndex, recipe.OutputStack);
			if (evidence.RootCandidate(recipe) is { } candidate)
				yield return Line(Text("RecipeAndRoute", name, local, Text("Candidate." + candidate.Status)));
			else if (key == view.Root && evidence.Plan.SearchStatistics.BudgetExhausted)
				yield return Line(Text("RecipeAndRoute", name, local, Text("Candidate.Deferred")));
			else
				yield return Line(Text("RecipeAndLocal", name, local));
		}
		if (producerCount > LineLimit)
			yield return Line(Text("MoreEntries", producerCount - LineLimit), Muted);

		IReadOnlyList<GraphViewLink> incoming = view.Incoming(key);
		if (incoming.Count == 0)
			yield break;
		yield return Line(Text("Requirements"), Section);
		foreach (GraphViewLink link in incoming.Take(LineLimit)) {
			IngredientEdge ingredient = link.Ingredient!;
			IngredientResolution amounts = evidence.Resolution.GetIngredientResolution(ingredient);
			yield return Line(Text("UsedBy", ingredient.Target.RecipeIndex, IngredientName(ingredient)));
			yield return Line(LocalAmounts(amounts), Muted);
		}
		if (incoming.Count > LineLimit)
			yield return Line(Text("MoreEntries", incoming.Count - LineLimit), Muted);
	}

	private static IEnumerable<UIElement> RecipeDetails(CraftGraphView view, RecipeNode recipe)
	{
		GraphViewEvidence evidence = view.Evidence;
		RecipeResolution local = evidence.Resolution.GetRecipeResolution(recipe);
		yield return Line(Text("Makes", Lang.GetItemNameValue(recipe.Result.ItemType), recipe.OutputStack));
		yield return Line(Text("LocalStatus", Text("Local." + local.State)));
		if (evidence.RootCandidate(recipe) is { } rootCandidate) {
			yield return Line(Text("RootCandidate", Text("Candidate." + rootCandidate.Status)), Section);
			yield return Line(Text("CandidateEvidence", Text("Plan." + rootCandidate.ResultStatus)), Muted);
		}
		else if (ReferenceEquals(recipe.Result, view.Subgraph.Target) && evidence.Plan.SearchStatistics.BudgetExhausted)
			yield return Line(Text("RootCandidate", Text("Candidate.Deferred")), Section);
		string[] dependencyStates = evidence.DependencyCandidates(recipe)
			.Select(candidate => Text("Candidate." + candidate.Status)).Distinct().ToArray();
		if (dependencyStates.Length > 0)
			yield return Line(Text("DependencyCandidate", string.Join(" / ", dependencyStates)), Muted);
		if (evidence.IsPlanned(recipe))
			yield return Line(Text("Planned", evidence.Steps(recipe).Sum(step => step.CraftRepetitions)), Section);

		yield return Line(Text("Requirements"), Section);
		if (local.Ingredients.Count == 0)
			yield return Line(Text("None"), Muted);
		foreach (IngredientResolution ingredient in local.Ingredients) {
			IngredientEdge edge = ingredient.Ingredient;
			yield return Line(IngredientName(edge));
			yield return Line(LocalAmounts(ingredient), Muted);
			foreach (PlannedIngredientUse use in evidence.Uses(edge)) {
				yield return Line(Text("LedgerAmounts", use.RequiredQuantity, use.AvailableBeforeExpansion, use.DeficitQuantity), Muted);
				if (use.GroupAllocation is { } allocation) {
					string items = string.Join(", ", allocation.ConcreteAllocations.Select(entry =>
						Text("Stack", Lang.GetItemNameValue(entry.ItemType), entry.Quantity)));
					yield return Line(Text("GroupAllocation", items), Section);
				}
			}
		}

		yield return Line(Text("Stations"), Section);
		if (local.Station.RequiredTileTypes.Count == 0)
			yield return Line(Text("None"), Muted);
		foreach (int tile in local.Station.RequiredTileTypes)
			yield return Line(Text("Constraint", StationName(tile), Text(local.Station.MissingTileTypes.Contains(tile) ? "Missing" : "Satisfied")));
		yield return Line(Text("Conditions"), Section);
		if (local.Conditions.Count == 0)
			yield return Line(Text("None"), Muted);
		foreach (ConditionConstraintResolution condition in local.Conditions)
			yield return Line(Text("Constraint", condition.Description, Text("Condition." + condition.State)));
	}

	private static string IngredientName(IngredientEdge edge) => edge.AcceptsRecipeGroup
		? Text("LogicalGroup", string.Join(" / ", edge.AcceptedRecipeGroups.Select(group => group.DisplayText)))
		: Lang.GetItemNameValue(edge.Source.ItemType);

	private static string LocalAmounts(IngredientResolution amounts) => Text(
		"LocalAmounts", amounts.RequiredAmount, amounts.AvailableAmount, Math.Max(0, amounts.RequiredAmount - amounts.AvailableAmount));

	private static string StationName(int tile)
	{
		string name = Lang.GetMapObjectName(MapHelper.TileToLookup(tile, 0));
		return string.IsNullOrWhiteSpace(name) ? TileID.Search.GetName(tile) ?? Text("TileId", tile) : name;
	}

	private sealed class DetailTitle : UIElement
	{
		private readonly string _title;
		private readonly int _itemType;

		public DetailTitle(string title, int itemType)
		{
			_title = title;
			_itemType = itemType;
			Width.Set(0, 1);
			Height.Set(36, 0);
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			Rectangle bounds = GetDimensions().ToRectangle();
			CraftGraphCanvas.Icon(spriteBatch, _itemType, new Vector2(bounds.X + 16, bounds.Center.Y), 29);
			CraftGraphCanvas.Label(spriteBatch, _title, new Vector2(bounds.X + 38, bounds.Y + 8),
				Color.White, 1.02f, bounds.Width - 39);
		}
	}
}
