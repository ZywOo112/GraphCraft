using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GraphCraft.Core.Graph;

public sealed class ItemNode : ICraftGraphNode
{
	private readonly List<RecipeNode> _producerRecipes = new();
	private readonly List<RecipeNode> _consumerRecipes = new();

	internal ItemNode(int itemType)
	{
		ItemType = itemType;
		ProducerRecipes = new ReadOnlyCollection<RecipeNode>(_producerRecipes);
		ConsumerRecipes = new ReadOnlyCollection<RecipeNode>(_consumerRecipes);
	}

	public string GraphId => $"item:{ItemType}";

	public CraftNodeKind NodeKind => CraftNodeKind.Item;

	public int ItemType { get; }

	public IReadOnlyList<RecipeNode> ProducerRecipes { get; }

	public IReadOnlyList<RecipeNode> ConsumerRecipes { get; }

	public ItemClassification Classification =>
		_producerRecipes.Count > 0 ? ItemClassification.Derived : ItemClassification.Atomic;

	internal void AddProducer(RecipeNode recipe) => _producerRecipes.Add(recipe);

	internal void AddConsumer(RecipeNode recipe) => _consumerRecipes.Add(recipe);
}
