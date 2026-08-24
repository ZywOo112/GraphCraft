using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Subgraphs;

public static class TargetSubgraphExtractor
{
	public static TargetCraftSubgraph Extract(
		CraftGraph graph,
		GraphAnalysisResult analysis,
		ItemNode target,
		TargetSubgraphOptions options)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(analysis);
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(options);
		ValidateInputs(graph, analysis, target);

		var timer = Stopwatch.StartNew();
		var itemNodes = new HashSet<ItemNode>();
		var recipeNodes = new HashSet<RecipeNode>();
		var ingredientEdges = new HashSet<IngredientEdge>();
		var productEdges = new HashSet<ProductEdge>();
		var depthByNode = new Dictionary<GraphNodeKey, int>();
		var includedComponentIds = new HashSet<int>();
		var processedItems = new HashSet<GraphNodeKey>();
		var pendingItems = new Queue<(ItemNode Item, int Depth)>();
		SubgraphTruncationReason truncationReason = SubgraphTruncationReason.None;
		int skippedFrontierCount = 0;

		AddItem(target, depth: 0);
		pendingItems.Enqueue((target, 0));

		while (pendingItems.Count > 0) {
			(ItemNode item, int depth) = pendingItems.Dequeue();
			GraphNodeKey itemKey = GraphNodeKey.From(item);
			if (!processedItems.Add(itemKey))
				continue;

			IReadOnlyList<RecipeNode> producers = item.ProducerRecipes;
			if (options.MaximumDependencyDepth is int maximumDepth && depth >= maximumDepth) {
				int omittedProducers = producers.Count(recipe => !recipeNodes.Contains(recipe));
				if (omittedProducers > 0) {
					truncationReason |= SubgraphTruncationReason.DepthLimit;
					skippedFrontierCount += omittedProducers;
				}
				continue;
			}

			foreach (RecipeNode producer in producers.OrderBy(recipe => recipe.RecipeIndex)) {
				if (recipeNodes.Contains(producer))
					continue;

				ItemNode[] dependencyItems = producer.Ingredients
					.SelectMany(edge => EnumerateDependencyItems(graph, edge))
					.Distinct()
					.ToArray();
				ItemNode[] newIngredientItems = dependencyItems
					.Where(ingredient => !itemNodes.Contains(ingredient))
					.ToArray();
				int requiredNewNodes = 1 + newIngredientItems.Length;
				if (itemNodes.Count + recipeNodes.Count + requiredNewNodes > options.MaximumGraphNodes) {
					truncationReason |= SubgraphTruncationReason.NodeLimit;
					skippedFrontierCount++;
					continue;
				}

				recipeNodes.Add(producer);
				productEdges.Add(producer.Product);
				AddDepth(producer, depth + 1);
				includedComponentIds.Add(analysis.GetComponentId(producer));

				foreach (IngredientEdge ingredient in producer.Ingredients) {
					ingredientEdges.Add(ingredient);
				}
				foreach (ItemNode dependencyItem in dependencyItems) {
					bool isNewItem = itemNodes.Add(dependencyItem);
					AddDepth(dependencyItem, depth + 1);
					includedComponentIds.Add(analysis.GetComponentId(dependencyItem));
					if (isNewItem)
						pendingItems.Enqueue((dependencyItem, depth + 1));
				}
			}
		}

		int alternativeProducerRecipeCount = itemNodes.Sum(item => {
			int includedProducerCount = item.ProducerRecipes.Count(recipeNodes.Contains);
			return Math.Max(0, includedProducerCount - 1);
		});
		int sharedItemNodeCount = itemNodes.Count(item =>
			item.ConsumerRecipes.Where(recipeNodes.Contains).Distinct().Skip(1).Any());

		timer.Stop();
		var result = new TargetCraftSubgraph(
			target,
			itemNodes,
			recipeNodes,
			ingredientEdges,
			productEdges,
			depthByNode,
			includedComponentIds,
			options,
			truncationReason,
			skippedFrontierCount,
			alternativeProducerRecipeCount,
			sharedItemNodeCount,
			timer.Elapsed);
		ValidateResult(graph, analysis, result);
		return result;

		void AddItem(ItemNode item, int depth)
		{
			itemNodes.Add(item);
			AddDepth(item, depth);
			includedComponentIds.Add(analysis.GetComponentId(item));
		}

		void AddDepth(ICraftGraphNode node, int depth)
		{
			GraphNodeKey key = GraphNodeKey.From(node);
			if (!depthByNode.TryGetValue(key, out int existingDepth) || depth < existingDepth)
				depthByNode[key] = depth;
		}
	}

	private static IEnumerable<ItemNode> EnumerateDependencyItems(CraftGraph graph, IngredientEdge ingredient)
	{
		yield return ingredient.Source;
		foreach (int itemType in ingredient.AcceptedRecipeGroups
			.SelectMany(group => group.AcceptedItemTypes)
			.Distinct()
			.OrderBy(itemType => itemType)) {
			if (graph.ItemsByType.TryGetValue(itemType, out ItemNode? item))
				yield return item;
		}
	}

	private static void ValidateInputs(CraftGraph graph, GraphAnalysisResult analysis, ItemNode target)
	{
		if (!graph.IsSealed)
			throw new InvalidOperationException("Target extraction requires a sealed CraftGraph.");

		int graphNodeCount = graph.ItemsByType.Count + graph.RecipesByIndex.Count;
		if (analysis.TotalNodeCount != graphNodeCount)
			throw new InvalidOperationException("GraphAnalysisResult does not describe the supplied CraftGraph.");

		if (!graph.ItemsByType.TryGetValue(target.ItemType, out ItemNode? canonicalTarget) || !ReferenceEquals(canonicalTarget, target))
			throw new InvalidOperationException("Target ItemNode must be the canonical instance owned by the supplied CraftGraph.");
	}

	private static void ValidateResult(CraftGraph graph, GraphAnalysisResult analysis, TargetCraftSubgraph result)
	{
		var graphIngredientEdges = new HashSet<IngredientEdge>(graph.IngredientEdges);
		var graphProductEdges = new HashSet<ProductEdge>(graph.ProductEdges);
		var includedItems = new HashSet<ItemNode>(result.ItemNodes);
		var includedRecipes = new HashSet<RecipeNode>(result.RecipeNodes);

		if (result.ItemNodes.Select(item => item.ItemType).Distinct().Count() != result.ItemNodes.Count)
			throw new InvalidOperationException("Target subgraph contains duplicate logical ItemNodes.");

		foreach (ItemNode item in result.ItemNodes) {
			if (!graph.ItemsByType.TryGetValue(item.ItemType, out ItemNode? canonical) || !ReferenceEquals(canonical, item))
				throw new InvalidOperationException($"Subgraph ItemNode {item.ItemType} is not owned by the source CraftGraph.");
			analysis.GetComponentId(item);
		}

		foreach (RecipeNode recipe in result.RecipeNodes) {
			if (!graph.RecipesByIndex.TryGetValue(recipe.RecipeIndex, out RecipeNode? canonical) || !ReferenceEquals(canonical, recipe))
				throw new InvalidOperationException($"Subgraph RecipeNode {recipe.RecipeIndex} is not owned by the source CraftGraph.");
			analysis.GetComponentId(recipe);
		}

		foreach (IngredientEdge edge in result.IngredientEdges) {
			if (!graphIngredientEdges.Contains(edge) || !includedItems.Contains(edge.Source) || !includedRecipes.Contains(edge.Target))
				throw new InvalidOperationException($"Invalid IngredientEdge {edge.GraphId} in target subgraph.");
		}

		foreach (ProductEdge edge in result.ProductEdges) {
			if (!graphProductEdges.Contains(edge) || !includedRecipes.Contains(edge.Source) || !includedItems.Contains(edge.Target))
				throw new InvalidOperationException($"Invalid ProductEdge {edge.GraphId} in target subgraph.");
		}
	}
}
