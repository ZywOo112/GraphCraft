using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Core.Resolution;

public sealed class ResolutionSnapshot
{
	private static long _nextResolutionId;

	internal ResolutionSnapshot(
		TargetCraftSubgraph sourceSubgraph,
		PlayerCraftContextSnapshot context,
		IDictionary<ItemNode, ItemResolution> itemResolutions,
		IDictionary<IngredientEdge, IngredientResolution> ingredientResolutions,
		IDictionary<RecipeNode, RecipeResolution> recipeResolutions,
		TimeSpan elapsed)
	{
		ResolutionId = Interlocked.Increment(ref _nextResolutionId);
		CreatedAtUtc = DateTimeOffset.UtcNow;
		SourceSubgraph = sourceSubgraph;
		Context = context;
		ItemResolutions = new ReadOnlyDictionary<ItemNode, ItemResolution>(
			new Dictionary<ItemNode, ItemResolution>(itemResolutions));
		IngredientResolutions = new ReadOnlyDictionary<IngredientEdge, IngredientResolution>(
			new Dictionary<IngredientEdge, IngredientResolution>(ingredientResolutions));
		RecipeResolutions = new ReadOnlyDictionary<RecipeNode, RecipeResolution>(
			new Dictionary<RecipeNode, RecipeResolution>(recipeResolutions));
		Elapsed = elapsed;
	}

	public long ResolutionId { get; }

	public DateTimeOffset CreatedAtUtc { get; }

	public ItemNode Target => SourceSubgraph.Target;

	public TargetCraftSubgraph SourceSubgraph { get; }

	public PlayerCraftContextSnapshot Context { get; }

	public IReadOnlyDictionary<ItemNode, ItemResolution> ItemResolutions { get; }

	public IReadOnlyDictionary<IngredientEdge, IngredientResolution> IngredientResolutions { get; }

	public IReadOnlyDictionary<RecipeNode, RecipeResolution> RecipeResolutions { get; }

	public TimeSpan Elapsed { get; }

	public ItemResolution GetItemResolution(ItemNode item) => ItemResolutions.TryGetValue(item, out ItemResolution? resolution)
		? resolution
		: throw new KeyNotFoundException($"ItemNode {item.GraphId} is not part of this resolution snapshot.");

	public IngredientResolution GetIngredientResolution(IngredientEdge edge) =>
		IngredientResolutions.TryGetValue(edge, out IngredientResolution? resolution)
			? resolution
			: throw new KeyNotFoundException($"IngredientEdge {edge.GraphId} is not part of this resolution snapshot.");

	public RecipeResolution GetRecipeResolution(RecipeNode recipe) =>
		RecipeResolutions.TryGetValue(recipe, out RecipeResolution? resolution)
			? resolution
			: throw new KeyNotFoundException($"RecipeNode {recipe.GraphId} is not part of this resolution snapshot.");
}
