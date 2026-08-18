using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Debug;

internal static partial class CraftGraphDiagnostics
{
	public static string FormatAnalysisSummary(GraphAnalysisResult analysis)
	{
		return $"""
		[GraphCraft] Graph analysis
		  TotalNodes: {analysis.TotalNodeCount}
		  TotalEdges: {analysis.TotalEdgeCount}
		  StronglyConnectedComponents: {analysis.ComponentCount}
		  CyclicComponents: {analysis.CyclicComponentCount}
		  AcyclicComponents: {analysis.AcyclicComponentCount}
		  NodesInCycles: {analysis.NodesParticipatingInCycles}
		  ItemNodesInCycles: {analysis.ItemNodesParticipatingInCycles}
		  RecipeNodesInCycles: {analysis.RecipeNodesParticipatingInCycles}
		  LargestComponentSize: {analysis.LargestComponentSize}
		  CondensationNodes: {analysis.CondensationGraph.ComponentCount}
		  CondensationEdges: {analysis.CondensationGraph.EdgeCount}
		  CondensationIsDag: {analysis.CondensationGraph.IsDag}
		  TopologicalOrderCount: {analysis.CondensationGraph.TopologicalOrder.Count}
		  SccElapsedMs: {analysis.SccElapsed.TotalMilliseconds:F3}
		  TotalAnalysisElapsedMs: {analysis.TotalElapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatCyclicComponentInspection(
		GraphAnalysisResult analysis,
		Func<int, string> itemName)
	{
		StronglyConnectedComponent? component = analysis.Components
			.Where(candidate => candidate.IsCyclic)
			.OrderByDescending(candidate => candidate.Size)
			.ThenBy(candidate => candidate.ComponentId)
			.FirstOrDefault();

		if (component is null)
			return "[GraphCraft] No cyclic strongly connected component was found.";

		var builder = new StringBuilder();
		builder.AppendLine($"[GraphCraft] Cyclic SCC #{component.ComponentId}, size={component.Size}");
		builder.AppendLine("  Items:");
		foreach (ItemNode item in component.ItemNodes.Take(12))
			builder.AppendLine($"    - {itemName(item.ItemType)} (ItemID {item.ItemType})");
		if (component.ItemNodes.Count > 12)
			builder.AppendLine($"    - ... {component.ItemNodes.Count - 12} more ItemNodes");

		builder.AppendLine("  Recipes:");
		foreach (RecipeNode recipe in component.RecipeNodes.Take(12))
			builder.AppendLine($"    - Recipe #{recipe.RecipeIndex} -> {itemName(recipe.Result.ItemType)} x{recipe.OutputStack}");
		if (component.RecipeNodes.Count > 12)
			builder.AppendLine($"    - ... {component.RecipeNodes.Count - 12} more RecipeNodes");

		IReadOnlyList<ICraftGraphNode> cycle = FindCycle(component);
		builder.AppendLine("  Cycle chain:");
		if (cycle.Count == 0) {
			builder.AppendLine("    - cycle path unavailable despite cyclic SCC classification");
		}
		else {
			builder.Append("    ");
			builder.AppendLine(string.Join(" -> ", cycle.Select(node => DescribeNode(node, itemName))));
		}

		return builder.ToString();
	}

	public static string FormatSubgraphSummary(
		TargetCraftSubgraph subgraph,
		GraphAnalysisResult analysis,
		Func<int, string> itemName)
	{
		int cyclicComponents = subgraph.IncludedComponentIds.Count(componentId => analysis.Components[componentId].IsCyclic);
		return $"""
		[GraphCraft] Target subgraph: {itemName(subgraph.Target.ItemType)} (ItemID {subgraph.Target.ItemType})
		  ItemNodes: {subgraph.ItemNodes.Count}
		  RecipeNodes: {subgraph.RecipeNodes.Count}
		  IngredientEdges: {subgraph.IngredientEdges.Count}
		  ProductEdges: {subgraph.ProductEdges.Count}
		  TotalEdges: {subgraph.TotalEdgeCount}
		  MaximumDependencyDepthReached: {subgraph.MaximumDependencyDepthReached}
		  AlternativeProducerRecipes: {subgraph.AlternativeProducerRecipeCount}
		  SharedItemNodes: {subgraph.SharedItemNodeCount}
		  IncludedComponents: {subgraph.IncludedComponentIds.Count}
		  IncludedCyclicComponents: {cyclicComponents}
		  IsTruncated: {subgraph.IsTruncated}
		  TruncationReason: {subgraph.TruncationReason}
		  SkippedFrontierCount: {subgraph.SkippedFrontierCount}
		  ExtractionElapsedMs: {subgraph.Elapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatTargetSubgraphGraph(
		TargetCraftSubgraph subgraph,
		GraphAnalysisResult analysis,
		Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		var visited = new HashSet<GraphNodeKey>();
		var includedRecipes = new HashSet<RecipeNode>(subgraph.RecipeNodes);
		var includedIngredientEdges = new HashSet<IngredientEdge>(subgraph.IngredientEdges);
		builder.AppendLine($"[GraphCraft] Target subgraph view: {itemName(subgraph.Target.ItemType)}");
		AppendItem(subgraph.Target, string.Empty, isLast: true, isRoot: true);

		if (subgraph.TruncationReason.HasFlag(SubgraphTruncationReason.NodeLimit))
			builder.AppendLine($"[node limit] skipped recipe frontiers: {subgraph.SkippedFrontierCount}");
		return builder.ToString();

		void AppendItem(ItemNode item, string prefix, bool isLast, bool isRoot = false, int? requiredAmount = null)
		{
			GraphNodeKey key = GraphNodeKey.From(item);
			string connector = isRoot ? string.Empty : isLast ? "└─ " : "├─ ";
			builder.Append(prefix).Append(connector);
			if (requiredAmount is int amount)
				builder.Append($"x{amount} ");
			builder.Append(DescribeItemWithComponent(item, analysis, itemName));
			if (!visited.Add(key)) {
				builder.AppendLine(" [shared node]");
				return;
			}
			builder.AppendLine();

			int depth = subgraph.DependencyDepthByNode[key];
			RecipeNode[] producers = item.ProducerRecipes.Where(includedRecipes.Contains).OrderBy(recipe => recipe.RecipeIndex).ToArray();
			string childPrefix = isRoot ? string.Empty : prefix + (isLast ? "   " : "│  ");
			if (producers.Length == 0 &&
				subgraph.Options.MaximumDependencyDepth is int maximumDepth &&
				depth >= maximumDepth &&
				item.ProducerRecipes.Count > 0) {
				builder.Append(childPrefix).AppendLine("└─ [depth limit]");
				return;
			}

			for (int index = 0; index < producers.Length; index++)
				AppendRecipe(producers[index], childPrefix, index == producers.Length - 1);
		}

		void AppendRecipe(RecipeNode recipe, string prefix, bool isLast)
		{
			GraphNodeKey key = GraphNodeKey.From(recipe);
			string connector = isLast ? "└─ " : "├─ ";
			builder.Append(prefix).Append(connector)
				.Append($"Recipe #{recipe.RecipeIndex} -> x{recipe.OutputStack}")
				.Append(DescribeComponentSuffix(key, analysis));
			if (!visited.Add(key)) {
				builder.AppendLine(" [shared node]");
				return;
			}
			builder.AppendLine();

			IngredientEdge[] ingredients = recipe.Ingredients.Where(includedIngredientEdges.Contains).ToArray();
			string childPrefix = prefix + (isLast ? "   " : "│  ");
			for (int index = 0; index < ingredients.Length; index++) {
				IngredientEdge ingredient = ingredients[index];
				bool ingredientIsLast = index == ingredients.Length - 1;
				AppendItem(ingredient.Source, childPrefix, ingredientIsLast, requiredAmount: ingredient.RequiredAmount);
			}
		}
	}

	private static string DescribeItemWithComponent(
		ItemNode item,
		GraphAnalysisResult analysis,
		Func<int, string> itemName)
	{
		GraphNodeKey key = GraphNodeKey.From(item);
		return $"{itemName(item.ItemType)} (ItemID {item.ItemType}) [{item.Classification}]" + DescribeComponentSuffix(key, analysis);
	}

	private static string DescribeComponentSuffix(GraphNodeKey key, GraphAnalysisResult analysis)
	{
		int componentId = analysis.GetComponentId(key);
		return analysis.Components[componentId].IsCyclic
			? $" [cycle component #{componentId}]"
			: $" [component #{componentId}]";
	}

	private static string DescribeNode(ICraftGraphNode node, Func<int, string> itemName) => node switch
	{
		ItemNode item => $"{itemName(item.ItemType)} (ItemID {item.ItemType})",
		RecipeNode recipe => $"Recipe #{recipe.RecipeIndex}",
		_ => node.GraphId
	};

	private static IReadOnlyList<ICraftGraphNode> FindCycle(StronglyConnectedComponent component)
	{
		var membersByKey = component.Members.ToDictionary(GraphNodeKey.From);
		var memberKeys = new HashSet<GraphNodeKey>(membersByKey.Keys);

		foreach (ICraftGraphNode sourceNode in component.Members) {
			GraphNodeKey source = GraphNodeKey.From(sourceNode);
			foreach (ICraftGraphNode targetNode in GetOutgoingNodes(sourceNode).Where(node => memberKeys.Contains(GraphNodeKey.From(node)))) {
				GraphNodeKey target = GraphNodeKey.From(targetNode);
				if (target == source)
					return new[] { sourceNode, sourceNode };

				var queue = new Queue<GraphNodeKey>();
				var parent = new Dictionary<GraphNodeKey, GraphNodeKey?> { [target] = null };
				queue.Enqueue(target);
				while (queue.Count > 0 && !parent.ContainsKey(source)) {
					GraphNodeKey current = queue.Dequeue();
					foreach (ICraftGraphNode neighborNode in GetOutgoingNodes(membersByKey[current])) {
						GraphNodeKey neighbor = GraphNodeKey.From(neighborNode);
						if (!memberKeys.Contains(neighbor) || parent.ContainsKey(neighbor))
							continue;
						parent[neighbor] = current;
						queue.Enqueue(neighbor);
					}
				}

				if (!parent.ContainsKey(source))
					continue;

				var reversePath = new List<GraphNodeKey>();
				GraphNodeKey? cursor = source;
				while (cursor is GraphNodeKey key) {
					reversePath.Add(key);
					cursor = parent[key];
				}
				reversePath.Reverse();

				var cycle = new List<ICraftGraphNode> { sourceNode };
				cycle.AddRange(reversePath.Select(key => membersByKey[key]));
				return cycle;
			}
		}

		return Array.Empty<ICraftGraphNode>();
	}

	private static IEnumerable<ICraftGraphNode> GetOutgoingNodes(ICraftGraphNode node) => node switch
	{
		ItemNode item => item.ConsumerRecipes,
		RecipeNode recipe => new ICraftGraphNode[] { recipe.Result },
		_ => Array.Empty<ICraftGraphNode>()
	};
}
