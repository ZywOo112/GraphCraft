namespace GraphCraft.Core.Graph;

public sealed class ProductEdge
{
	internal ProductEdge(RecipeNode source, ItemNode target, int outputAmount)
	{
		Source = source;
		Target = target;
		OutputAmount = outputAmount;
	}

	public string GraphId => $"product:{Source.RecipeIndex}";

	public CraftEdgeKind EdgeKind => CraftEdgeKind.Product;

	public RecipeNode Source { get; }

	public ItemNode Target { get; }

	public int OutputAmount { get; }
}
