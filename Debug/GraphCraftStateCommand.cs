using System;
using GraphCraft.Core.Context;
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

	public override string Usage => "/gcstate <itemId>";

	public override string Description => "Captures the current player crafting context and resolves a bounded GraphCraft target overlay.";

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		if (args.Length != 1 || !int.TryParse(args[0], out int itemType) || itemType <= 0)
			throw new UsageException("Usage: /gcstate <itemId>");

		if (CraftGraphSystem.Current is null || CraftGraphSystem.CurrentAnalysis is null)
			throw new InvalidOperationException("GraphCraft static graph analysis is not available.");

		TargetCraftSubgraph subgraph = CraftGraphSystem.ExtractTarget(
			itemType,
			TargetSubgraphOptions.Bounded(maximumDependencyDepth: 2, maximumGraphNodes: 250));
		PlayerCraftContextSnapshot context = PlayerCraftContextCapture.Capture(caller.Player, CraftGraphSystem.Current);
		ResolutionSnapshot resolution = ResolutionOverlayBuilder.Build(subgraph, CraftGraphSystem.CurrentAnalysis, context);
		string output = CraftResolutionDiagnostics.FormatResolutionSnapshot(resolution, Lang.GetItemNameValue);
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			caller.Reply(line.TrimEnd('\r'));
	}
}
