using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GraphCraft.Core.Graph;

public sealed class CraftGraph
{
	private readonly Dictionary<int, ItemNode> _itemsByType = new();
	private readonly Dictionary<int, RecipeNode> _recipesByIndex = new();
	private readonly List<IngredientEdge> _ingredientEdges = new();
	private readonly List<ProductEdge> _productEdges = new();
	private bool _isSealed;

	internal CraftGraph()
	{
		ItemsByType = new ReadOnlyDictionary<int, ItemNode>(_itemsByType);
		RecipesByIndex = new ReadOnlyDictionary<int, RecipeNode>(_recipesByIndex);
		IngredientEdges = new ReadOnlyCollection<IngredientEdge>(_ingredientEdges);
		ProductEdges = new ReadOnlyCollection<ProductEdge>(_productEdges);
	}

	public IReadOnlyDictionary<int, ItemNode> ItemsByType { get; }

	public IReadOnlyDictionary<int, RecipeNode> RecipesByIndex { get; }

	public IReadOnlyList<IngredientEdge> IngredientEdges { get; }

	public IReadOnlyList<ProductEdge> ProductEdges { get; }

	public bool IsSealed => _isSealed;

	public bool TryGetItemNode(int itemType, out ItemNode? node) =>
		_itemsByType.TryGetValue(itemType, out node);

	public bool TryGetRecipeNode(int recipeIndex, out RecipeNode? node) =>
		_recipesByIndex.TryGetValue(recipeIndex, out node);

	public CraftGraphStatistics GetStatistics()
	{
		EnsureSealed();

		int atomicCount = _itemsByType.Values.Count(item => item.Classification == ItemClassification.Atomic);
		int derivedCount = _itemsByType.Count - atomicCount;
		int recipeCount = _recipesByIndex.Count;

		return new CraftGraphStatistics(
			ItemNodeCount: _itemsByType.Count,
			RecipeNodeCount: recipeCount,
			IngredientEdgeCount: _ingredientEdges.Count,
			ProductEdgeCount: _productEdges.Count,
			AtomicItemCount: atomicCount,
			DerivedItemCount: derivedCount,
			ItemsWithMultipleProducerRecipes: _itemsByType.Values.Count(item => item.ProducerRecipes.Count > 1),
			MaximumProducerRecipeCount: _itemsByType.Values.Max(item => item.ProducerRecipes.Count),
			AverageIngredientsPerRecipe: recipeCount == 0 ? 0 : (double)_ingredientEdges.Count / recipeCount,
			RecipesWithConditions: _recipesByIndex.Values.Count(recipe => recipe.Constraints.HasConditions),
			RecipesWithRequiredTiles: _recipesByIndex.Values.Count(recipe => recipe.Constraints.HasRequiredTiles));
	}

	internal ItemNode GetOrAddItem(int itemType)
	{
		EnsureMutable();

		if (!_itemsByType.TryGetValue(itemType, out ItemNode? item)) {
			item = new ItemNode(itemType);
			_itemsByType.Add(itemType, item);
		}

		return item;
	}

	internal RecipeNode AddRecipe(
		int recipeIndex,
		int resultItemType,
		int outputStack,
		RecipeConstraintSet constraints)
	{
		EnsureMutable();

		if (recipeIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(recipeIndex), "RecipeIndex must be assigned after recipe setup.");

		if (_recipesByIndex.ContainsKey(recipeIndex))
			throw new InvalidOperationException($"Duplicate RecipeIndex {recipeIndex}.");

		ItemNode result = GetOrAddItem(resultItemType);
		var recipe = new RecipeNode(recipeIndex, result, outputStack, constraints);
		_recipesByIndex.Add(recipeIndex, recipe);
		_productEdges.Add(recipe.Product);
		result.AddProducer(recipe);
		return recipe;
	}

	internal void AddIngredient(
		RecipeNode recipe,
		int itemType,
		int requiredAmount,
		IReadOnlyList<RecipeGroupInfo> acceptedRecipeGroups)
	{
		EnsureMutable();

		ItemNode source = GetOrAddItem(itemType);
		var edge = new IngredientEdge(
			recipe.Ingredients.Count,
			source,
			recipe,
			requiredAmount,
			acceptedRecipeGroups);

		recipe.AddIngredient(edge);
		source.AddConsumer(recipe);
		_ingredientEdges.Add(edge);
	}

	internal void Seal()
	{
		EnsureMutable();

		if (_recipesByIndex.Count != _productEdges.Count)
			throw new InvalidOperationException("Every RecipeNode must have exactly one product edge.");

		if (_itemsByType.Count == 0 || _recipesByIndex.Count == 0)
			throw new InvalidOperationException("The materialized CraftGraph cannot be empty.");

		_isSealed = true;

		CraftGraphStatistics statistics = GetStatistics();
		if (statistics.AtomicItemCount + statistics.DerivedItemCount != statistics.ItemNodeCount)
			throw new InvalidOperationException("Atomic and derived ItemNode counts do not cover the graph.");
	}

	private void EnsureMutable()
	{
		if (_isSealed)
			throw new InvalidOperationException("The CraftGraph is sealed and read-only.");
	}

	private void EnsureSealed()
	{
		if (!_isSealed)
			throw new InvalidOperationException("The CraftGraph must be sealed before it is queried.");
	}
}
