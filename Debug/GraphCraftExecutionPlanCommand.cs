using System;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using GraphCraft.Core.Subgraphs;
using GraphCraft.Systems;
using Terraria;
using Terraria.ModLoader;

namespace GraphCraft.Debug;

public sealed class GraphCraftExecutionPlanCommand : ModCommand
{
	public override string Command => "gcexecplan";

	public override CommandType Type => CommandType.Chat;

	public override string Usage => "/gcexecplan <itemId> [additionalQuantity]";

	public override string Description => "Plans and freshly revalidates a read-only craft transaction against real inventory and environment evidence.";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int itemType) || itemType <= 0)
			throw new UsageException(Usage);
		int additionalQuantity = 1;
		if (args.Length == 2 && (!int.TryParse(args[1], out additionalQuantity) || additionalQuantity <= 0))
			throw new UsageException(Usage);

		if (CraftGraphSystem.Current is null || CraftGraphSystem.CurrentAnalysis is null)
			throw new InvalidOperationException("GraphCraft static graph analysis is not available.");

		TargetCraftSubgraph subgraph = CraftGraphSystem.ExtractTarget(
			itemType,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 6, maximumGraphNodes: 1500));
		PlayerCraftContextSnapshot planningContext = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
		ResolutionSnapshot planningResolution = ResolutionOverlayBuilder.Build(subgraph, CraftGraphSystem.CurrentAnalysis, planningContext);
		TargetCraftPlanResult logicalPlan = TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			subgraph,
			planningResolution,
			subgraph.Target,
			additionalQuantity,
			VirtualResourceLedger.Create(planningContext.Resources)));

		CraftExecutionPreflightResult? preflight = null;
		PlayerCraftContextSnapshot? freshContext = null;
		if (logicalPlan.Succeeded) {
			freshContext = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
			ResolutionSnapshot freshResolution = ResolutionOverlayBuilder.Build(subgraph, CraftGraphSystem.CurrentAnalysis, freshContext);
			preflight = CraftExecutionPlanner.CreatePreflight(new CraftExecutionPreflightRequest(logicalPlan, freshResolution));
		}

		PlayerCraftContextSnapshot afterContext = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
		bool inventoryUnchanged = SameInventory(freshContext ?? planningContext, afterContext);
		string output = CraftResolutionDiagnostics.FormatExecutionPreflightAudit(
			logicalPlan,
			preflight,
			inventoryUnchanged,
			Lang.GetItemNameValue);
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			caller.Reply(line.TrimEnd('\r'));
	}

	private static bool SameInventory(PlayerCraftContextSnapshot before, PlayerCraftContextSnapshot after)
	{
		CraftResourceSourceSnapshot? beforeInventory = before.ResourceSources.SingleOrDefault(source =>
			source.Descriptor.SourceKind == CraftResourceSourceKind.Inventory);
		CraftResourceSourceSnapshot? afterInventory = after.ResourceSources.SingleOrDefault(source =>
			source.Descriptor.SourceKind == CraftResourceSourceKind.Inventory);
		return beforeInventory is not null && afterInventory is not null &&
			beforeInventory.Descriptor.SourceId == afterInventory.Descriptor.SourceId &&
			beforeInventory.ResourceCounts.OrderBy(pair => pair.Key)
				.SequenceEqual(afterInventory.ResourceCounts.OrderBy(pair => pair.Key));
	}
}
