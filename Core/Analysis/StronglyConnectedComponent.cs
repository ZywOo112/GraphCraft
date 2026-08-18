using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Analysis;

public sealed class StronglyConnectedComponent
{
	internal StronglyConnectedComponent(int componentId, IEnumerable<ICraftGraphNode> members, bool isCyclic)
	{
		ComponentId = componentId;
		ICraftGraphNode[] orderedMembers = members
			.OrderBy(member => member.NodeKind)
			.ThenBy(member => GraphNodeKey.From(member).LocalId)
			.ToArray();

		Members = new ReadOnlyCollection<ICraftGraphNode>(orderedMembers);
		ItemNodes = new ReadOnlyCollection<ItemNode>(orderedMembers.OfType<ItemNode>().ToArray());
		RecipeNodes = new ReadOnlyCollection<RecipeNode>(orderedMembers.OfType<RecipeNode>().ToArray());
		IsCyclic = isCyclic;
	}

	public int ComponentId { get; }

	public IReadOnlyList<ICraftGraphNode> Members { get; }

	public IReadOnlyList<ItemNode> ItemNodes { get; }

	public IReadOnlyList<RecipeNode> RecipeNodes { get; }

	public int Size => Members.Count;

	public bool IsCyclic { get; }
}
