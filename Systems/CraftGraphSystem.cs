using System.Diagnostics;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Subgraphs;
using GraphCraft.Debug;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GraphCraft.Systems;

public sealed class CraftGraphSystem : ModSystem
{
	public static CraftGraph? Current { get; private set; }

	public static GraphAnalysisResult? CurrentAnalysis { get; private set; }

	public override void PostSetupRecipes()
	{
		var graphBuildTimer = Stopwatch.StartNew();
		Current = CraftGraphBuilder.Build(Main.recipe);
		graphBuildTimer.Stop();

		CraftGraphStatistics statistics = Current.GetStatistics();
		Mod.Logger.Info(CraftGraphDiagnostics.FormatStatistics(statistics));
		Mod.Logger.Info($"[GraphCraft] GraphBuildElapsedMs: {graphBuildTimer.Elapsed.TotalMilliseconds:F3}");

		CurrentAnalysis = GraphAnalyzer.Analyze(Current);
		Mod.Logger.Info(CraftGraphDiagnostics.FormatAnalysisSummary(CurrentAnalysis));
		Mod.Logger.Info(CraftGraphDiagnostics.FormatCyclicComponentInspection(CurrentAnalysis, Lang.GetItemNameValue));

		TargetCraftSubgraph furnaceSubgraph = ExtractTarget(
			ItemID.Furnace,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 2, maximumGraphNodes: 250));
		Mod.Logger.Info("[GraphCraft] Furnace validates the same direct recipe used in Milestone 1, now through TargetSubgraphExtractor.");
		Mod.Logger.Info(CraftGraphDiagnostics.FormatSubgraphSummary(furnaceSubgraph, CurrentAnalysis, Lang.GetItemNameValue));
		Mod.Logger.Info(CraftGraphDiagnostics.FormatTargetSubgraphGraph(furnaceSubgraph, CurrentAnalysis, Lang.GetItemNameValue));

		TargetCraftSubgraph complexSubgraph = ExtractTarget(
			ItemID.CellPhone,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 3, maximumGraphNodes: 180));
		Mod.Logger.Info("[GraphCraft] Cell Phone is the complex vanilla validation target because it has a deep multi-part crafting dependency structure.");
		Mod.Logger.Info(CraftGraphDiagnostics.FormatSubgraphSummary(complexSubgraph, CurrentAnalysis, Lang.GetItemNameValue));
		Mod.Logger.Info(CraftGraphDiagnostics.FormatTargetSubgraphGraph(complexSubgraph, CurrentAnalysis, Lang.GetItemNameValue));

		TargetCraftSubgraph nodeLimitedSubgraph = ExtractTarget(
			ItemID.CellPhone,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 3, maximumGraphNodes: 10));
		if (!nodeLimitedSubgraph.TruncationReason.HasFlag(SubgraphTruncationReason.NodeLimit))
			throw new System.InvalidOperationException("The Cell Phone node-limit validation did not reach the configured graph-node limit.");
		Mod.Logger.Info("[GraphCraft] Node-limit safety validation passed for Cell Phone with MaximumGraphNodes=10.");
		Mod.Logger.Info(CraftGraphDiagnostics.FormatSubgraphSummary(nodeLimitedSubgraph, CurrentAnalysis, Lang.GetItemNameValue));
		Mod.Logger.Info(CraftGraphDiagnostics.FormatTargetSubgraphGraph(nodeLimitedSubgraph, CurrentAnalysis, Lang.GetItemNameValue));

		DynamicResolutionSyntheticReport dynamicReport = DynamicResolutionSyntheticTests.Run(
			Current,
			CurrentAnalysis,
			furnaceSubgraph,
			complexSubgraph);
		Mod.Logger.Info(CraftResolutionDiagnostics.FormatSyntheticValidation(dynamicReport, Lang.GetItemNameValue));

		PlannerFoundationSyntheticReport plannerReport = PlannerFoundationSyntheticTests.Run();
		Mod.Logger.Info(CraftResolutionDiagnostics.FormatPlannerFoundationValidation(plannerReport));

		RecursivePlannerSyntheticReport recursiveReport = RecursivePlannerSyntheticTests.Run();
		Mod.Logger.Info(CraftResolutionDiagnostics.FormatRecursivePlannerValidation(recursiveReport));
	}

	public override void Unload()
	{
		CurrentAnalysis = null;
		Current = null;
	}

	public static TargetCraftSubgraph ExtractTarget(int itemType, TargetSubgraphOptions options)
	{
		if (Current is null || CurrentAnalysis is null)
			throw new System.InvalidOperationException("CraftGraph and GraphAnalysisResult must be available before target extraction.");

		if (!Current.TryGetItemNode(itemType, out ItemNode? target) || target is null)
			throw new System.InvalidOperationException($"Validation target ItemID {itemType} is missing from CraftGraph.");

		return TargetSubgraphExtractor.Extract(Current, CurrentAnalysis, target, options);
	}
}
