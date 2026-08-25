using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using Terraria;

namespace GraphCraft.Systems.Execution;

public sealed class LivePlayerFinalValidationProvider : ICraftExecutionFinalValidationProvider
{
	private readonly Player _player;
	private readonly CraftGraph _graph;
	private readonly GraphAnalysisResult _analysis;

	public LivePlayerFinalValidationProvider(Player player, CraftGraph graph, GraphAnalysisResult analysis)
	{
		_player = player ?? throw new ArgumentNullException(nameof(player));
		_graph = graph ?? throw new ArgumentNullException(nameof(graph));
		_analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
	}

	public CraftExecutionFinalValidationResult Validate(ExecutableTransactionPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var context = PlayerCraftContextCapture.Capture(_player, _graph);
		ResolutionSnapshot resolution = ResolutionOverlayBuilder.Build(
			plan.LogicalPlan.Request.TargetSubgraph,
			_analysis,
			context);
		return CraftExecutionPlanner.RevalidateExistingPlan(plan, resolution);
	}
}

public sealed class CraftResourceTransactionAdapterRegistry : ICraftResourceTransactionAdapterRegistry
{
	private readonly IReadOnlyDictionary<CraftResourceSourceId, ICraftResourceTransactionAdapter> _adapters;

	public CraftResourceTransactionAdapterRegistry(IEnumerable<ICraftResourceTransactionAdapter> adapters)
	{
		ArgumentNullException.ThrowIfNull(adapters);
		_adapters = adapters.ToDictionary(adapter => adapter.Descriptor.SourceId);
	}

	public bool TryGetAdapter(
		CraftResourceSourceId sourceId,
		out ICraftResourceTransactionAdapter? adapter) =>
		_adapters.TryGetValue(sourceId, out adapter);
}
