using System;
using System.Collections.Generic;
using GraphCraft.Core.Resources;
using Terraria;

namespace GraphCraft.Systems.Adapters;

public sealed class VanillaPlayerInventoryResourceSource : ICraftResourceSource
{
	private readonly Player _player;

	public VanillaPlayerInventoryResourceSource(Player player)
	{
		_player = player ?? throw new ArgumentNullException(nameof(player));
		Descriptor = new CraftResourceSourceDescriptor(
			new CraftResourceSourceId($"vanilla:player-inventory:{player.whoAmI}"),
			CraftResourceSourceKind.Inventory,
			"Vanilla Player Inventory",
			CraftResourceConsumptionCapability.AdapterTransactionRequired);
	}

	public CraftResourceSourceDescriptor Descriptor { get; }

	public CraftResourceSourceSnapshot Capture()
	{
		var counts = new Dictionary<int, int>();
		int recipeInventorySlots = Math.Min(58, _player.inventory.Length);
		for (int slot = 0; slot < recipeInventorySlots; slot++) {
			Item item = _player.inventory[slot];
			if (item is null || item.IsAir || item.stack <= 0)
				continue;
			counts[item.type] = checked(counts.GetValueOrDefault(item.type) + item.stack);
		}

		return new CraftResourceSourceSnapshot(Descriptor, counts);
	}
}
