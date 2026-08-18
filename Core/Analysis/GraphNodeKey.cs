using System;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Analysis;

public readonly record struct GraphNodeKey(CraftNodeKind NodeKind, int LocalId)
{
	public static GraphNodeKey From(ICraftGraphNode node) => node switch
	{
		ItemNode item => new GraphNodeKey(CraftNodeKind.Item, item.ItemType),
		RecipeNode recipe => new GraphNodeKey(CraftNodeKind.Recipe, recipe.RecipeIndex),
		_ => throw new ArgumentException($"Unsupported CraftGraph node type {node.GetType().FullName}.", nameof(node))
	};

	public override string ToString() => $"{NodeKind}:{LocalId}";
}
