using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GraphCraft.Core.Graph;

public sealed class RecipeNode : ICraftGraphNode
{
	private readonly List<IngredientEdge> _ingredients = new();

	internal RecipeNode(
		int recipeIndex,
		ItemNode result,
		int outputStack,
		RecipeConstraintSet constraints)
	{
		RecipeIndex = recipeIndex;
		Result = result;
		OutputStack = outputStack;
		Constraints = constraints;
		Ingredients = new ReadOnlyCollection<IngredientEdge>(_ingredients);
		Product = new ProductEdge(this, result, outputStack);
	}

	public string GraphId => $"recipe:{RecipeIndex}";

	public CraftNodeKind NodeKind => CraftNodeKind.Recipe;

	public int RecipeIndex { get; }

	public ItemNode Result { get; }

	public int OutputStack { get; }

	public IReadOnlyList<IngredientEdge> Ingredients { get; }

	public ProductEdge Product { get; }

	public RecipeConstraintSet Constraints { get; }

	internal void AddIngredient(IngredientEdge ingredient) => _ingredients.Add(ingredient);
}
