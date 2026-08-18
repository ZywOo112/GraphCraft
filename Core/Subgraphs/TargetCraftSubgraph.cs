using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Subgraphs;

[Flags]
public enum SubgraphTruncationReason
{
	None = 0,
	DepthLimit = 1,
	NodeLimit = 2
}

public sealed class TargetSubgraphOptions
{
	private TargetSubgraphOptions(int? maximumDependencyDepth, int maximumGraphNodes)
	{
		if (maximumDependencyDepth < 0)
			throw new ArgumentOutOfRangeException(nameof(maximumDependencyDepth));
		if (maximumGraphNodes <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumGraphNodes));

		MaximumDependencyDepth = maximumDependencyDepth;
		MaximumGraphNodes = maximumGraphNodes;
	}

	public int? MaximumDependencyDepth { get; }

	public int MaximumGraphNodes { get; }

	public bool IsFullReachableMode => MaximumDependencyDepth is null && MaximumGraphNodes == int.MaxValue;

	public static TargetSubgraphOptions Bounded(int maximumDependencyDepth, int maximumGraphNodes) =>
		new(maximumDependencyDepth, maximumGraphNodes);

	public static TargetSubgraphOptions FullReachable() => new(null, int.MaxValue);
}

public sealed class TargetCraftSubgraph
{
	internal TargetCraftSubgraph(
		ItemNode target,
		IEnumerable<ItemNode> itemNodes,
		IEnumerable<RecipeNode> recipeNodes,
		IEnumerable<IngredientEdge> ingredientEdges,
		IEnumerable<ProductEdge> productEdges,
		IDictionary<GraphNodeKey, int> dependencyDepthByNode,
		IEnumerable<int> includedComponentIds,
		TargetSubgraphOptions options,
		SubgraphTruncationReason truncationReason,
		int skippedFrontierCount,
		int alternativeProducerRecipeCount,
		int sharedItemNodeCount,
		TimeSpan elapsed)
	{
		Target = target;
		ItemNodes = new ReadOnlyCollection<ItemNode>(itemNodes.OrderBy(item => item.ItemType).ToArray());
		RecipeNodes = new ReadOnlyCollection<RecipeNode>(recipeNodes.OrderBy(recipe => recipe.RecipeIndex).ToArray());
		IngredientEdges = new ReadOnlyCollection<IngredientEdge>(ingredientEdges
			.OrderBy(edge => edge.Target.RecipeIndex)
			.ThenBy(edge => edge.Ordinal)
			.ToArray());
		ProductEdges = new ReadOnlyCollection<ProductEdge>(productEdges
			.OrderBy(edge => edge.Source.RecipeIndex)
			.ToArray());
		DependencyDepthByNode = new ReadOnlyDictionary<GraphNodeKey, int>(
			new Dictionary<GraphNodeKey, int>(dependencyDepthByNode));
		IncludedComponentIds = new ReadOnlyCollection<int>(includedComponentIds.OrderBy(id => id).ToArray());
		Options = options;
		TruncationReason = truncationReason;
		SkippedFrontierCount = skippedFrontierCount;
		AlternativeProducerRecipeCount = alternativeProducerRecipeCount;
		SharedItemNodeCount = sharedItemNodeCount;
		Elapsed = elapsed;
	}

	public ItemNode Target { get; }

	public IReadOnlyList<ItemNode> ItemNodes { get; }

	public IReadOnlyList<RecipeNode> RecipeNodes { get; }

	public IReadOnlyList<IngredientEdge> IngredientEdges { get; }

	public IReadOnlyList<ProductEdge> ProductEdges { get; }

	public IReadOnlyDictionary<GraphNodeKey, int> DependencyDepthByNode { get; }

	public IReadOnlyList<int> IncludedComponentIds { get; }

	public TargetSubgraphOptions Options { get; }

	public SubgraphTruncationReason TruncationReason { get; }

	public int SkippedFrontierCount { get; }

	public int AlternativeProducerRecipeCount { get; }

	public int SharedItemNodeCount { get; }

	public TimeSpan Elapsed { get; }

	public int TotalNodeCount => ItemNodes.Count + RecipeNodes.Count;

	public int TotalEdgeCount => IngredientEdges.Count + ProductEdges.Count;

	public int MaximumDependencyDepthReached => ItemNodes.Count == 0
		? 0
		: ItemNodes.Max(item => DependencyDepthByNode[GraphNodeKey.From(item)]);

	public bool IsTruncated => TruncationReason != SubgraphTruncationReason.None;

	public bool ContainsNode(ICraftGraphNode node) => DependencyDepthByNode.ContainsKey(GraphNodeKey.From(node));
}
