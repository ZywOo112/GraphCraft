using System;
using GraphCraft.Core.Context;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;
using GraphCraft.Systems;
using GraphCraft.Systems.Execution;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GraphCraft.Debug;

public sealed class GraphCraftExecuteCommand : ModCommand
{
	public override string Command => "gcexecute";

	public override CommandType Type => CommandType.Chat;

	public override string Usage => "/gcexecute <itemId> [additionalQuantity] confirm";

	public override string Description => "DANGER: really consumes inventory and delivers crafted outputs through an M5.2 transaction; requires confirm.";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		if (!TryParse(args, out int itemType, out int additionalQuantity))
			throw new UsageException($"{Usage} — THIS COMMAND MUTATES INVENTORY.");

		if (Main.netMode != NetmodeID.SinglePlayer || caller.Player.whoAmI != Main.myPlayer) {
			caller.Reply("[GraphCraft] TransactionStatus=UnsupportedMultiplayerContext; mutation=false. M5.2 live execution is single-player/local-authority only.");
			return;
		}

		if (CraftGraphSystem.Current is null || CraftGraphSystem.CurrentAnalysis is null)
			throw new InvalidOperationException("GraphCraft static graph analysis is not available.");

		TargetCraftSubgraph subgraph = CraftGraphSystem.ExtractTarget(
			itemType,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 6, maximumGraphNodes: 1500));
		PlayerCraftContextSnapshot planningContext = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
		ResolutionSnapshot planningResolution = ResolutionOverlayBuilder.Build(
			subgraph,
			CraftGraphSystem.CurrentAnalysis,
			planningContext);
		TargetCraftPlanResult logicalPlan = TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			subgraph,
			planningResolution,
			subgraph.Target,
			additionalQuantity,
			VirtualResourceLedger.Create(planningContext.Resources)));

		CraftExecutionPreflightResult? preflight = null;
		CraftTransactionResult? transactionResult = null;
		if (logicalPlan.Succeeded) {
			PlayerCraftContextSnapshot preflightContext = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
			ResolutionSnapshot preflightResolution = ResolutionOverlayBuilder.Build(
				subgraph,
				CraftGraphSystem.CurrentAnalysis,
				preflightContext);
			preflight = CraftExecutionPlanner.CreatePreflight(new(logicalPlan, preflightResolution));
			if (preflight.TransactionPlan is not null) {
				var vanillaAdapter = new VanillaInventoryTransactionAdapter(caller.Player);
				var transaction = new CraftExecutionTransaction(
					preflight.TransactionPlan,
					new LivePlayerFinalValidationProvider(caller.Player, CraftGraphSystem.Current, CraftGraphSystem.CurrentAnalysis),
					new CraftResourceTransactionAdapterRegistry(new[] { vanillaAdapter }),
					new TerrariaRecipeExecutionSemantics(),
					new VanillaInventoryOutputSink(caller.Player, vanillaAdapter));
				transactionResult = transaction.Execute();
			}
		}

		string output = CraftResolutionDiagnostics.FormatCraftTransactionAudit(
			logicalPlan,
			preflight,
			transactionResult,
			Lang.GetItemNameValue);
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			caller.Reply(line.TrimEnd('\r'));
	}

	private static bool TryParse(string[] args, out int itemType, out int additionalQuantity)
	{
		itemType = 0;
		additionalQuantity = 1;
		if (args.Length is < 2 or > 3 || !int.TryParse(args[0], out itemType) || itemType <= 0 ||
			!string.Equals(args[^1], "confirm", StringComparison.OrdinalIgnoreCase))
			return false;
		return args.Length != 3 ||
			(int.TryParse(args[1], out additionalQuantity) && additionalQuantity > 0);
	}
}
