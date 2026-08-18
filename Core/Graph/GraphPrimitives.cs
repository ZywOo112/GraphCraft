namespace GraphCraft.Core.Graph;

public enum CraftNodeKind
{
	Item,
	Recipe
}

public enum CraftEdgeKind
{
	Ingredient,
	Product
}

public enum ItemClassification
{
	Atomic,
	Derived
}

public interface ICraftGraphNode
{
	string GraphId { get; }

	CraftNodeKind NodeKind { get; }
}
