using System;
using System.Collections.Generic;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Subgraphs;
using GraphCraft.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace GraphCraft.UI;

internal sealed class CraftGraphUISystem : ModSystem
{
	internal const int MaximumQuantity = 9999;
	private UserInterface? _interface;
	private CraftGraphWindow? _window;
	private ModKeybind? _toggle;
	private GameTime _time = new();
	private int _hoveredItem;

	public override void Load()
	{
		if (Main.dedServ)
			return;
		_interface = new UserInterface();
		_toggle = KeybindLoader.RegisterKeybind(Mod, "Craft Graph", Keys.G);
	}

	public override void OnWorldUnload()
	{
		Close();
		_window = null;
		_hoveredItem = 0;
	}

	public override void Unload()
	{
		OnWorldUnload();
		_interface = null;
		_toggle = null;
	}

	internal static string Text(string key, params object[] args) =>
		Language.GetTextValue("Mods.GraphCraft.GraphUI." + key, args);

	internal void Toggle()
	{
		if (_toggle?.JustPressed != true || _interface is null || Main.gameMenu || Main.drawingPlayerChat)
			return;
		if (_interface.CurrentState is not null) {
			if (_window?.Typing != true)
				Close();
			return;
		}
		if (!Main.playerInventory || !OpenTarget(_hoveredItem, 1))
			OpenWindow();
	}

	internal bool OpenTarget(int itemType, int quantity)
	{
		if (_interface is null || Main.gameMenu || quantity is < 1 or > MaximumQuantity ||
			CraftGraphSystem.Current is not { } graph || CraftGraphSystem.CurrentAnalysis is not { } analysis ||
			!graph.TryGetItemNode(itemType, out ItemNode? target) || target is null)
			return false;

		TargetCraftSubgraph subgraph = TargetSubgraphExtractor.Extract(
			graph, analysis, target, TargetSubgraphOptions.Bounded(10, 1200));
		var view = new CraftGraphView(subgraph, analysis);
		Refresh(view, quantity);
		OpenWindow(refresh: false);
		_window!.SetTarget(view);
		return true;
	}

	internal static void Refresh(CraftGraphView view, int quantity) =>
		view.Refresh(PlayerCraftContextCapture.CaptureForSubgraph(Main.LocalPlayer, view.Subgraph), quantity);

	private void OpenWindow(bool refresh = true)
	{
		_window ??= new CraftGraphWindow(this);
		_interface!.SetState(_window);
		if (refresh)
			_window.Refresh();
	}

	internal void Close() => _interface?.SetState(null);

	public override void UpdateUI(GameTime gameTime)
	{
		_time = gameTime;
		_interface?.Update(gameTime);
	}

	public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
	{
		int mouseText = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
		var layer = new LegacyGameInterfaceLayer("GraphCraft: Craft Graph", () => {
			// Inventory item slots publish the hovered Item during their draw pass.
			_hoveredItem = Main.playerInventory && !string.IsNullOrEmpty(Main.hoverItemName) &&
				Main.HoverItem is { IsAir: false } item ? item.type : 0;
			_interface?.Draw(Main.spriteBatch, _time);
			return true;
		}, InterfaceScaleType.UI);
		layers.Insert(mouseText < 0 ? layers.Count : mouseText, layer);
	}
}

internal sealed class CraftGraphUIPlayer : ModPlayer
{
	public override void ProcessTriggers(TriggersSet triggersSet) => ModContent.GetInstance<CraftGraphUISystem>().Toggle();
}

public sealed class GraphCraftGraphCommand : ModCommand
{
	public override string Command => "gcgraph";
	public override CommandType Type => CommandType.Chat;
	public override string Usage => "/gcgraph <itemId> [additionalQuantity]";
	public override string Description => CraftGraphUISystem.Text("CommandDescription");

	public override void Action(CommandCaller caller, string input, string[] args)
	{
		int quantity = 1;
		if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int itemType) ||
			(args.Length == 2 && !int.TryParse(args[1], out quantity)))
			throw new UsageException(Usage);
		if (!ModContent.GetInstance<CraftGraphUISystem>().OpenTarget(itemType, quantity))
			caller.Reply(CraftGraphUISystem.Text("InvalidTarget", CraftGraphUISystem.MaximumQuantity));
	}
}
