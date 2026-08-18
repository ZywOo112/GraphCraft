using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Analysis;

public sealed class GraphAnalysisResult
{
	internal GraphAnalysisResult(
		int totalNodeCount,
		int totalEdgeCount,
		IEnumerable<StronglyConnectedComponent> components,
		IDictionary<GraphNodeKey, int> componentByNode,
		CondensationGraph condensationGraph,
		TimeSpan sccElapsed,
		TimeSpan totalElapsed)
	{
		TotalNodeCount = totalNodeCount;
		TotalEdgeCount = totalEdgeCount;
		Components = new ReadOnlyCollection<StronglyConnectedComponent>(components
			.OrderBy(component => component.ComponentId)
			.ToArray());
		ComponentByNode = new ReadOnlyDictionary<GraphNodeKey, int>(
			new Dictionary<GraphNodeKey, int>(componentByNode));
		CondensationGraph = condensationGraph;
		SccElapsed = sccElapsed;
		TotalElapsed = totalElapsed;
	}

	public int TotalNodeCount { get; }

	public int TotalEdgeCount { get; }

	public IReadOnlyList<StronglyConnectedComponent> Components { get; }

	public IReadOnlyDictionary<GraphNodeKey, int> ComponentByNode { get; }

	public CondensationGraph CondensationGraph { get; }

	public TimeSpan SccElapsed { get; }

	public TimeSpan TotalElapsed { get; }

	public int ComponentCount => Components.Count;

	public int CyclicComponentCount => Components.Count(component => component.IsCyclic);

	public int AcyclicComponentCount => ComponentCount - CyclicComponentCount;

	public int NodesParticipatingInCycles => Components.Where(component => component.IsCyclic).Sum(component => component.Size);

	public int ItemNodesParticipatingInCycles => Components.Where(component => component.IsCyclic).Sum(component => component.ItemNodes.Count);

	public int RecipeNodesParticipatingInCycles => Components.Where(component => component.IsCyclic).Sum(component => component.RecipeNodes.Count);

	public int LargestComponentSize => Components.Max(component => component.Size);

	public int GetComponentId(ICraftGraphNode node) => GetComponentId(GraphNodeKey.From(node));

	public int GetComponentId(GraphNodeKey key) => ComponentByNode.TryGetValue(key, out int componentId)
		? componentId
		: throw new KeyNotFoundException($"Node {key} is not present in this graph analysis.");

	public StronglyConnectedComponent GetComponent(ICraftGraphNode node) => Components[GetComponentId(node)];
}
