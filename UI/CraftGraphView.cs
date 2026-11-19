using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.UI;

internal sealed record GraphViewLink(
	GraphNodeKey Parent,
	GraphNodeKey Dependency,
	IngredientEdge? Ingredient,
	bool InCycle);

internal sealed record GraphViewNode(GraphNodeKey Key, int Column, int Row);

internal sealed record GraphViewLayout(
	IReadOnlyList<GraphViewNode> Nodes,
	IReadOnlyList<GraphViewLink> Links,
	bool HitDisplayLimit);

internal enum GraphRouteState
{
	Craftable,
	ChoiceRequired,
	NeedsIngredients,
	BlockedByStation,
	BlockedByCondition,
	UnknownConstraint,
	Deferred,
	NoRoute
}

internal sealed class CraftGraphView
{
	internal const int DefaultNodeLimit = 72;
	internal const int DefaultLinkLimit = 160;
	private static readonly RecursivePlanningLimits PlanningLimits = new(maximumBranchExpansions: 2048);
	private readonly GraphAnalysisResult _analysis;
	private readonly Dictionary<GraphNodeKey, ICraftGraphNode> _nodes;
	private readonly Dictionary<GraphNodeKey, List<GraphViewLink>> _outgoing = new();
	private readonly Dictionary<GraphNodeKey, List<GraphViewLink>> _incoming = new();
	private readonly HashSet<GraphNodeKey> _expanded = new();
	private readonly Stack<GraphNodeKey> _history = new();

	public CraftGraphView(TargetCraftSubgraph subgraph, GraphAnalysisResult analysis)
	{
		Subgraph = subgraph;
		_analysis = analysis;
		Root = Focus = Selection = GraphNodeKey.From(subgraph.Target);
		_nodes = subgraph.ItemNodes.Cast<ICraftGraphNode>().Concat(subgraph.RecipeNodes)
			.ToDictionary(GraphNodeKey.From);
		foreach (ProductEdge edge in subgraph.ProductEdges)
			AddLink(GraphNodeKey.From(edge.Target), GraphNodeKey.From(edge.Source), null);
		foreach (IngredientEdge edge in subgraph.IngredientEdges) {
			GraphNodeKey parent = GraphNodeKey.From(edge.Target);
			if (!edge.AcceptsRecipeGroup) {
				AddLink(parent, GraphNodeKey.From(edge.Source), edge);
				continue;
			}
			foreach (int itemType in edge.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().Order()) {
				var member = new GraphNodeKey(CraftNodeKind.Item, itemType);
				if (_nodes.ContainsKey(member))
					AddLink(parent, member, edge);
			}
		}
		_expanded.Add(Root);
		foreach (GraphViewLink producer in Outgoing(Root))
			_expanded.Add(producer.Dependency);
	}

	public TargetCraftSubgraph Subgraph { get; }
	public GraphNodeKey Root { get; }
	public GraphNodeKey Focus { get; private set; }
	public GraphNodeKey Selection { get; private set; }
	public bool CanGoBack => _history.Count > 0;
	public GraphViewEvidence Evidence { get; private set; } = null!;

	public ICraftGraphNode Node(GraphNodeKey key) => _nodes[key];
	public IReadOnlyList<GraphViewLink> Outgoing(GraphNodeKey key) =>
		_outgoing.TryGetValue(key, out List<GraphViewLink>? links) ? links : Array.Empty<GraphViewLink>();
	public IReadOnlyList<GraphViewLink> Incoming(GraphNodeKey key) =>
		_incoming.TryGetValue(key, out List<GraphViewLink>? links) ? links : Array.Empty<GraphViewLink>();
	public bool IsExpanded(GraphNodeKey key) => _expanded.Contains(key);
	public bool IsCyclic(GraphNodeKey key) => _analysis.Components[_analysis.GetComponentId(key)].IsCyclic;
	public bool IsShared(GraphNodeKey key) => key.NodeKind == CraftNodeKind.Item &&
		Incoming(key).Select(link => link.Parent).Distinct().Skip(1).Any();

	public void Refresh(PlayerCraftContextSnapshot context, int additionalQuantity)
	{
		ResolutionSnapshot resolution = ResolutionOverlayBuilder.Build(Subgraph, _analysis, context);
		TargetCraftPlanResult plan = TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			Subgraph, resolution, Subgraph.Target, additionalQuantity, VirtualResourceLedger.Create(context.Resources), PlanningLimits));
		Evidence = new GraphViewEvidence(resolution, plan);
	}

	public void Select(GraphNodeKey key)
	{
		if (_nodes.ContainsKey(key))
			Selection = key;
	}

	public void FocusSelection()
	{
		if (Selection == Focus)
			return;
		_history.Push(Focus);
		Focus = Selection;
		_expanded.Add(Focus);
	}

	public void Back()
	{
		if (_history.TryPop(out GraphNodeKey previous))
			Focus = Selection = previous;
	}

	public void ReturnToRoot()
	{
		_history.Clear();
		Focus = Selection = Root;
	}

	public bool ToggleExpansion(GraphNodeKey key)
	{
		if (Outgoing(key).Count == 0)
			return false;
		if (!_expanded.Remove(key))
			_expanded.Add(key);
		return true;
	}

	public GraphViewLayout Layout(int nodeLimit = DefaultNodeLimit, int linkLimit = DefaultLinkLimit)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeLimit);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(linkLimit);
		var pending = new Queue<GraphViewNode>();
		var seen = new HashSet<GraphNodeKey> { Focus };
		var nodes = new List<GraphViewNode>(nodeLimit);
		var links = new List<GraphViewLink>();
		var nextRow = new Dictionary<int, int>();
		bool hitLimit = false;
		pending.Enqueue(new GraphViewNode(Focus, 0, 0));
		while (pending.TryDequeue(out GraphViewNode? node)) {
			nodes.Add(node);
			if (!IsExpanded(node.Key))
				continue;

			int column = node.Column + 1;
			foreach (GraphViewLink link in Outgoing(node.Key)) {
				bool knownDependency = seen.Contains(link.Dependency);
				if (links.Count >= linkLimit || (!knownDependency && seen.Count >= nodeLimit)) {
					hitLimit = true;
					continue;
				}

				links.Add(link);
				if (knownDependency)
					continue;
				seen.Add(link.Dependency);
				int row = nextRow.GetValueOrDefault(column);
				nextRow[column] = row + 1;
				pending.Enqueue(new GraphViewNode(link.Dependency, column, row));
			}
		}

		return new GraphViewLayout(nodes, links, hitLimit);
	}

	private void AddLink(GraphNodeKey parent, GraphNodeKey dependency, IngredientEdge? ingredient)
	{
		int component = _analysis.GetComponentId(parent);
		// Conditional group membership is a projected relation, not a canonical SCC edge.
		bool canonical = ingredient is null || dependency == GraphNodeKey.From(ingredient.Source);
		var link = new GraphViewLink(parent, dependency, ingredient,
			canonical && _analysis.Components[component].IsCyclic && component == _analysis.GetComponentId(dependency));
		if (!_outgoing.TryGetValue(parent, out List<GraphViewLink>? outgoing))
			_outgoing.Add(parent, outgoing = new List<GraphViewLink>());
		if (!_incoming.TryGetValue(dependency, out List<GraphViewLink>? incoming))
			_incoming.Add(dependency, incoming = new List<GraphViewLink>());
		outgoing.Add(link);
		incoming.Add(link);
	}
}

internal sealed class GraphViewEvidence
{
	private readonly ILookup<RecipeNode, PlannedCraftStep> _steps;
	private readonly ILookup<IngredientEdge, PlannedIngredientUse> _uses;
	private readonly Dictionary<RecipeNode, RootRecipeCandidateResult> _rootCandidates;
	private readonly ILookup<RecipeNode, AlternativeCandidateResult> _dependencyCandidates;

	public GraphViewEvidence(ResolutionSnapshot resolution, TargetCraftPlanResult plan)
	{
		Resolution = resolution;
		Plan = plan;
		_steps = plan.Steps.ToLookup(step => step.Recipe);
		_uses = plan.Steps.SelectMany(step => step.Ingredients).ToLookup(use => use.Ingredient);
		_rootCandidates = plan.RootDecision?.Candidates.ToDictionary(candidate => candidate.Recipe) ?? new();
		IEnumerable<AlternativeDecision> decisions = plan.RecipeDecisions;
		if (plan.PendingRecipeDecision is { } pending)
			decisions = decisions.Append(pending);
		_dependencyCandidates = decisions.Distinct().SelectMany(decision => decision.Candidates)
			.ToLookup(candidate => candidate.Recipe);
	}

	public ResolutionSnapshot Resolution { get; }
	public TargetCraftPlanResult Plan { get; }
	public GraphRouteState Route {
		get {
			GraphRouteState route = Classify(Plan.Status);
			return route == GraphRouteState.NoRoute &&
				(Resolution.SourceSubgraph.IsTruncated || Plan.SearchStatistics.BudgetExhausted)
				? GraphRouteState.Deferred
				: route;
		}
	}
	public bool IsPlanned(RecipeNode recipe) => _steps.Contains(recipe);
	public IEnumerable<PlannedCraftStep> Steps(RecipeNode recipe) => _steps[recipe];
	public IEnumerable<PlannedIngredientUse> Uses(IngredientEdge ingredient) => _uses[ingredient];
	public RootRecipeCandidateResult? RootCandidate(RecipeNode recipe) => _rootCandidates.GetValueOrDefault(recipe);
	public IEnumerable<AlternativeCandidateResult> DependencyCandidates(RecipeNode recipe) => _dependencyCandidates[recipe];

	public bool IsPlanned(GraphViewLink link, CraftGraphView view) =>
		link.Ingredient is { } ingredient
			? _uses[ingredient].Any(use => use.GroupAllocation is null
				? !ingredient.AcceptsRecipeGroup
				: use.GroupAllocation.ConcreteAllocations.Any(allocation => allocation.ItemType == link.Dependency.LocalId))
			: _steps.Contains((RecipeNode)view.Node(link.Dependency));

	internal static GraphRouteState Classify(RecursivePlanStatus status) => status switch {
		RecursivePlanStatus.Success => GraphRouteState.Craftable,
		RecursivePlanStatus.UserChoiceRequired => GraphRouteState.ChoiceRequired,
		RecursivePlanStatus.InsufficientTerminalResource => GraphRouteState.NeedsIngredients,
		RecursivePlanStatus.BlockedByStation => GraphRouteState.BlockedByStation,
		RecursivePlanStatus.BlockedByCondition => GraphRouteState.BlockedByCondition,
		RecursivePlanStatus.UnknownConstraint => GraphRouteState.UnknownConstraint,
		RecursivePlanStatus.RecipeOutsideTargetSubgraph or
		RecursivePlanStatus.TargetSubgraphTruncated or RecursivePlanStatus.SearchBudgetBoundary or
		RecursivePlanStatus.PlanningDepthBoundary or RecursivePlanStatus.CycleBoundary or
		RecursivePlanStatus.AlternativeProducerDeferred or RecursivePlanStatus.AlternativeResolutionDeferred or
		RecursivePlanStatus.DeferredRecipeGroupAllocation => GraphRouteState.Deferred,
		_ => GraphRouteState.NoRoute
	};
}
