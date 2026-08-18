using GraphCraft.Core.Graph;
using GraphCraft.Debug;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace GraphCraft.Systems;

public sealed class CraftGraphSystem : ModSystem
{
	public static CraftGraph? Current { get; private set; }

	public override void PostSetupRecipes()
	{
		Current = CraftGraphBuilder.Build(Main.recipe);

		CraftGraphStatistics statistics = Current.GetStatistics();
		Mod.Logger.Info(CraftGraphDiagnostics.FormatStatistics(statistics));

		const int targetItemType = ItemID.Furnace;
		Mod.Logger.Info(CraftGraphDiagnostics.FormatTargetInspection(
			Current,
			targetItemType,
			Lang.GetItemNameValue,
			tileType => TileID.Search.GetName(tileType)));
		Mod.Logger.Info(CraftGraphDiagnostics.FormatDependencyGraph(
			Current,
			targetItemType,
			maximumRecipeDepth: 2,
			Lang.GetItemNameValue));
	}

	public override void Unload()
	{
		Current = null;
	}
}
