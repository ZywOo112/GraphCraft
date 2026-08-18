using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GraphCraft.Core.Analysis;

public readonly record struct CondensationEdge(int SourceComponentId, int TargetComponentId);

public sealed class CondensationGraph
{
	internal CondensationGraph(
		int componentCount,
		IEnumerable<CondensationEdge> edges,
		IEnumerable<int> topologicalOrder,
		bool isDag)
	{
		ComponentCount = componentCount;
		Edges = new ReadOnlyCollection<CondensationEdge>(edges
			.OrderBy(edge => edge.SourceComponentId)
			.ThenBy(edge => edge.TargetComponentId)
			.ToArray());
		TopologicalOrder = new ReadOnlyCollection<int>(topologicalOrder.ToArray());
		IsDag = isDag;

		var outgoing = Enumerable.Range(0, componentCount)
			.ToDictionary(componentId => componentId, _ => new List<int>());
		var incoming = Enumerable.Range(0, componentCount)
			.ToDictionary(componentId => componentId, _ => new List<int>());

		foreach (CondensationEdge edge in Edges) {
			outgoing[edge.SourceComponentId].Add(edge.TargetComponentId);
			incoming[edge.TargetComponentId].Add(edge.SourceComponentId);
		}

		OutgoingComponentIds = new ReadOnlyDictionary<int, IReadOnlyList<int>>(outgoing.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<int>)new ReadOnlyCollection<int>(pair.Value)));
		IncomingComponentIds = new ReadOnlyDictionary<int, IReadOnlyList<int>>(incoming.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<int>)new ReadOnlyCollection<int>(pair.Value)));
	}

	public int ComponentCount { get; }

	public IReadOnlyList<CondensationEdge> Edges { get; }

	public IReadOnlyDictionary<int, IReadOnlyList<int>> OutgoingComponentIds { get; }

	public IReadOnlyDictionary<int, IReadOnlyList<int>> IncomingComponentIds { get; }

	public IReadOnlyList<int> TopologicalOrder { get; }

	public bool IsDag { get; }

	public int EdgeCount => Edges.Count;
}
