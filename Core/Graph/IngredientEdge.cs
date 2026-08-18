using System.Collections.Generic;

namespace GraphCraft.Core.Graph;

public sealed class IngredientEdge
{
	internal IngredientEdge(
		int ordinal,
		ItemNode source,
		RecipeNode target,
		int requiredAmount,
		IReadOnlyList<RecipeGroupInfo> acceptedRecipeGroups)
	{
		Ordinal = ordinal;
		Source = source;
		Target = target;
		RequiredAmount = requiredAmount;
		AcceptedRecipeGroups = acceptedRecipeGroups;
	}

	public string GraphId => $"ingredient:{Target.RecipeIndex}:{Ordinal}";

	public CraftEdgeKind EdgeKind => CraftEdgeKind.Ingredient;

	public int Ordinal { get; }

	public ItemNode Source { get; }

	public RecipeNode Target { get; }

	public int RequiredAmount { get; }

	public IReadOnlyList<RecipeGroupInfo> AcceptedRecipeGroups { get; }

	public bool AcceptsRecipeGroup => AcceptedRecipeGroups.Count > 0;
}
