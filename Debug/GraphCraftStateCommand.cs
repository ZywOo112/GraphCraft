using System;
using GraphCraft.Core.Context;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;
using GraphCraft.Systems;
using Terraria;
using Terraria.ModLoader;

namespace GraphCraft.Debug;

public sealed class GraphCraftStateCommand : ModCommand
{
	public override string Command => "gcstate";

	public override CommandType Type => CommandType.Chat;

	public override string Usage => "/gcstate <itemId> [additionalQuantity]";

	public override string Description => "Captures current crafting evidence and runs a bounded target-level GraphCraft plan audit.";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int itemType) || itemType <= 0 ||
			(args.Length == 2 && (!int.TryParse(args[1], out _) || int.Parse(args[1]) <= 0)))
			throw new UsageException("Usage: /gcstate <itemId> [additionalQuantity]");
		int additionalQuantity = args.Length == 2 ? int.Parse(args[1]) : 1;

		if (CraftGraphSystem.Current is null || CraftGraphSystem.CurrentAnalysis is null)
			throw new InvalidOperationException("GraphCraft static graph analysis is not available.");

		TargetCraftSubgraph subgraph = CraftGraphSystem.ExtractTarget(
			itemType,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 6, maximumGraphNodes: 1500));
		PlayerCraftContextSnapshot context = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
		ResolutionSnapshot resolution = ResolutionOverlayBuilder.Build(subgraph, CraftGraphSystem.CurrentAnalysis, context);
		TargetCraftPlanResult plan = TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			subgraph,
			resolution,
			subgraph.Target,
			additionalQuantity,
			VirtualResourceLedger.Create(context.Resources)));
		string output = CraftResolutionDiagnostics.FormatTargetPlanAudit(plan, Lang.GetItemNameValue);
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			caller.Reply(line.TrimEnd('\r'));
	}
}
