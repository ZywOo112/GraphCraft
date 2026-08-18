using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Analysis;

public static class GraphAnalyzer
{
	public static GraphAnalysisResult Analyze(CraftGraph graph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		if (!graph.IsSealed)
			throw new InvalidOperationException("Graph analysis requires a sealed CraftGraph.");

		var totalTimer = Stopwatch.StartNew();
		TopologySnapshot topology = CreateTopology(graph);

		var sccTimer = Stopwatch.StartNew();
		(IReadOnlyList<StronglyConnectedComponent> components, Dictionary<GraphNodeKey, int> componentByNode) =
			FindStronglyConnectedComponents(topology);
		sccTimer.Stop();

		ValidateComponentCoverage(topology, components, componentByNode);
		CondensationGraph condensationGraph = BuildCondensationGraph(topology, components.Count, componentByNode);
		ValidateTopologicalOrder(condensationGraph);

		totalTimer.Stop();
		return new GraphAnalysisResult(
			topology.NodesByKey.Count,
			topology.EdgeCount,
			components,
			componentByNode,
			condensationGraph,
			sccTimer.Elapsed,
			totalTimer.Elapsed);
	}

	private static TopologySnapshot CreateTopology(CraftGraph graph)
	{
		var nodesByKey = new Dictionary<GraphNodeKey, ICraftGraphNode>();
		var outgoing = new Dictionary<GraphNodeKey, List<GraphNodeKey>>();

		foreach (ItemNode item in graph.ItemsByType.Values.OrderBy(item => item.ItemType))
			AddNode(item);
		foreach (RecipeNode recipe in graph.RecipesByIndex.Values.OrderBy(recipe => recipe.RecipeIndex))
			AddNode(recipe);

		foreach (IngredientEdge edge in graph.IngredientEdges)
			outgoing[GraphNodeKey.From(edge.Source)].Add(GraphNodeKey.From(edge.Target));
		foreach (ProductEdge edge in graph.ProductEdges)
			outgoing[GraphNodeKey.From(edge.Source)].Add(GraphNodeKey.From(edge.Target));

		int edgeCount = outgoing.Values.Sum(neighbors => neighbors.Count);
		int expectedEdgeCount = graph.IngredientEdges.Count + graph.ProductEdges.Count;
		if (edgeCount != expectedEdgeCount)
			throw new InvalidOperationException($"Topology edge count {edgeCount} does not match CraftGraph edge count {expectedEdgeCount}.");

		return new TopologySnapshot(nodesByKey, outgoing, edgeCount);

		void AddNode(ICraftGraphNode node)
		{
			GraphNodeKey key = GraphNodeKey.From(node);
			if (!nodesByKey.TryAdd(key, node))
				throw new InvalidOperationException($"Duplicate graph node identity {key}.");
			outgoing.Add(key, new List<GraphNodeKey>());
		}
	}

	private static (IReadOnlyList<StronglyConnectedComponent>, Dictionary<GraphNodeKey, int>)
		FindStronglyConnectedComponents(TopologySnapshot topology)
	{
		var indexByNode = new Dictionary<GraphNodeKey, int>();
		var lowLinkByNode = new Dictionary<GraphNodeKey, int>();
		var tarjanStack = new Stack<GraphNodeKey>();
		var onTarjanStack = new HashSet<GraphNodeKey>();
		var components = new List<StronglyConnectedComponent>();
		var componentByNode = new Dictionary<GraphNodeKey, int>();
		int nextIndex = 0;

		foreach (GraphNodeKey root in topology.NodesByKey.Keys
			.OrderBy(key => key.NodeKind)
			.ThenBy(key => key.LocalId)) {
			if (indexByNode.ContainsKey(root))
				continue;

			var dfsFrames = new Stack<TarjanFrame>();
			dfsFrames.Push(new TarjanFrame(root, parent: null));

			while (dfsFrames.Count > 0) {
				TarjanFrame frame = dfsFrames.Peek();
				GraphNodeKey current = frame.Node;

				if (!frame.IsInitialized) {
					frame.IsInitialized = true;
					indexByNode.Add(current, nextIndex);
					lowLinkByNode.Add(current, nextIndex);
					nextIndex++;
					tarjanStack.Push(current);
					onTarjanStack.Add(current);
				}

				IReadOnlyList<GraphNodeKey> neighbors = topology.Outgoing[current];
				if (frame.NextNeighborIndex < neighbors.Count) {
					GraphNodeKey neighbor = neighbors[frame.NextNeighborIndex++];
					if (!indexByNode.ContainsKey(neighbor)) {
						dfsFrames.Push(new TarjanFrame(neighbor, current));
						continue;
					}

					if (onTarjanStack.Contains(neighbor))
						lowLinkByNode[current] = Math.Min(lowLinkByNode[current], indexByNode[neighbor]);
					continue;
				}

				dfsFrames.Pop();
				if (frame.Parent is GraphNodeKey parent)
					lowLinkByNode[parent] = Math.Min(lowLinkByNode[parent], lowLinkByNode[current]);

				if (lowLinkByNode[current] != indexByNode[current])
					continue;

				var memberKeys = new List<GraphNodeKey>();
				GraphNodeKey member;
				do {
					member = tarjanStack.Pop();
					onTarjanStack.Remove(member);
					memberKeys.Add(member);
				} while (member != current);

				bool hasSelfLoop = memberKeys.Count == 1 && topology.Outgoing[memberKeys[0]].Contains(memberKeys[0]);
				int componentId = components.Count;
				var component = new StronglyConnectedComponent(
					componentId,
					memberKeys.Select(key => topology.NodesByKey[key]),
					isCyclic: memberKeys.Count > 1 || hasSelfLoop);
				components.Add(component);

				foreach (GraphNodeKey key in memberKeys)
					componentByNode.Add(key, componentId);
			}
		}

		return (components, componentByNode);
	}

	private static CondensationGraph BuildCondensationGraph(
		TopologySnapshot topology,
		int componentCount,
		IReadOnlyDictionary<GraphNodeKey, int> componentByNode)
	{
		var edges = new HashSet<CondensationEdge>();
		foreach ((GraphNodeKey source, List<GraphNodeKey> targets) in topology.Outgoing) {
			int sourceComponentId = componentByNode[source];
			foreach (GraphNodeKey target in targets) {
				int targetComponentId = componentByNode[target];
				if (sourceComponentId != targetComponentId)
					edges.Add(new CondensationEdge(sourceComponentId, targetComponentId));
			}
		}

		var outgoing = Enumerable.Range(0, componentCount)
			.ToDictionary(componentId => componentId, _ => new List<int>());
		var indegree = Enumerable.Range(0, componentCount)
			.ToDictionary(componentId => componentId, _ => 0);
		foreach (CondensationEdge edge in edges) {
			outgoing[edge.SourceComponentId].Add(edge.TargetComponentId);
			indegree[edge.TargetComponentId]++;
		}

		var ready = new SortedSet<int>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
		var topologicalOrder = new List<int>(componentCount);
		while (ready.Count > 0) {
			int componentId = ready.Min;
			ready.Remove(componentId);
			topologicalOrder.Add(componentId);

			foreach (int targetComponentId in outgoing[componentId].OrderBy(id => id)) {
				indegree[targetComponentId]--;
				if (indegree[targetComponentId] == 0)
					ready.Add(targetComponentId);
			}
		}

		bool isDag = topologicalOrder.Count == componentCount;
		return new CondensationGraph(componentCount, edges, topologicalOrder, isDag);
	}

	private static void ValidateComponentCoverage(
		TopologySnapshot topology,
		IReadOnlyList<StronglyConnectedComponent> components,
		IReadOnlyDictionary<GraphNodeKey, int> componentByNode)
	{
		if (componentByNode.Count != topology.NodesByKey.Count)
			throw new InvalidOperationException("Every CraftGraph node must map to exactly one strongly connected component.");

		int memberCount = components.Sum(component => component.Size);
		if (memberCount != topology.NodesByKey.Count)
			throw new InvalidOperationException("SCC member counts do not cover every CraftGraph node exactly once.");

		foreach (GraphNodeKey key in topology.NodesByKey.Keys) {
			if (!componentByNode.TryGetValue(key, out int componentId) || componentId < 0 || componentId >= components.Count)
				throw new InvalidOperationException($"Invalid SCC mapping for node {key}.");
		}
	}

	private static void ValidateTopologicalOrder(CondensationGraph graph)
	{
		if (!graph.IsDag)
			throw new InvalidOperationException("The SCC condensation graph contains a directed cycle.");

		var position = graph.TopologicalOrder
			.Select((componentId, index) => (componentId, index))
			.ToDictionary(pair => pair.componentId, pair => pair.index);

		foreach (CondensationEdge edge in graph.Edges) {
			if (position[edge.SourceComponentId] >= position[edge.TargetComponentId])
				throw new InvalidOperationException($"Invalid condensation topological order for edge {edge}.");
		}
	}

	private sealed class TarjanFrame
	{
		public TarjanFrame(GraphNodeKey node, GraphNodeKey? parent)
		{
			Node = node;
			Parent = parent;
		}

		public GraphNodeKey Node { get; }

		public GraphNodeKey? Parent { get; }

		public int NextNeighborIndex { get; set; }

		public bool IsInitialized { get; set; }
	}

	private sealed record TopologySnapshot(
		IReadOnlyDictionary<GraphNodeKey, ICraftGraphNode> NodesByKey,
		IReadOnlyDictionary<GraphNodeKey, List<GraphNodeKey>> Outgoing,
		int EdgeCount);
}
