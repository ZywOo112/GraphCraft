using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Execution;
using Terraria;
using Terraria.ModLoader;

namespace GraphCraft.Systems.Execution;

public sealed class VanillaInventoryOutputSink : ICraftOutputSink
{
	public const int GeneralInventorySlotCount = 50;
	private readonly Player _player;
	private readonly VanillaInventoryTransactionAdapter _adapter;

	public VanillaInventoryOutputSink(Player player, VanillaInventoryTransactionAdapter adapter)
	{
		_player = player ?? throw new ArgumentNullException(nameof(player));
		_adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
	}

	public bool CanAccept(CraftOutputCapacityRequest request, out string? issue)
	{
		ArgumentNullException.ThrowIfNull(request);
		Item[] simulation = _adapter.CaptureInventory();
		var consumedByPrepared = new Dictionary<CraftPreparedSourceAllocation, long>();
		foreach (CraftPreparedExternalConsumption consumption in request.ExternalConsumptions) {
			long offset = consumedByPrepared.GetValueOrDefault(consumption.PreparedAllocation);
			if (!SimulateConsumption(simulation, consumption.PreparedAllocation, offset, consumption.Quantity)) {
				issue = $"Prepared source locator cannot simulate ItemType {consumption.ItemType} x{consumption.Quantity}.";
				return false;
			}
			consumedByPrepared[consumption.PreparedAllocation] = checked(offset + consumption.Quantity);
		}

		foreach (CraftTransactionItemStack output in request.Outputs) {
			if (!TryGetItem(output, out Item? runtimeItem, out issue))
				return false;
			Item remaining = runtimeItem.Clone();
			if (!TryInsertSimulation(simulation, remaining)) {
				issue = $"Player inventory has insufficient capacity for ItemType {output.ItemType} x{output.Quantity}.";
				return false;
			}
		}

		issue = null;
		return true;
	}

	public IReadOnlyList<CraftTransactionItemStack> Deliver(
		IReadOnlyList<CraftTransactionItemStack> outputs,
		CraftMutationJournal journal,
		Action<int> afterMutation)
	{
		ArgumentNullException.ThrowIfNull(outputs);
		ArgumentNullException.ThrowIfNull(journal);
		ArgumentNullException.ThrowIfNull(afterMutation);
		Item[] before = _adapter.CaptureInventory();
		journal.Record(new DelegateCraftMutationJournalEntry(
			"Restore vanilla inventory before output delivery",
			() => _adapter.RestoreInventory(before)));

		int mutationOrdinal = 0;
		foreach (CraftTransactionItemStack output in outputs) {
			if (!TryGetItem(output, out Item? runtimeItem, out string? issue))
				throw new InvalidOperationException(issue);
			Item remaining = runtimeItem.Clone();
			for (int slot = 0; slot < GeneralSlotCount && !remaining.IsAir; slot++) {
				Item destination = _player.inventory[slot];
				if (destination.IsAir || !ItemLoader.CanStack(destination, remaining))
					continue;
				if (ItemLoader.TryStackItems(destination, remaining, out int transferred, infiniteSource: false) && transferred > 0)
					afterMutation(mutationOrdinal++);
			}
			for (int slot = 0; slot < GeneralSlotCount && !remaining.IsAir; slot++) {
				if (!_player.inventory[slot].IsAir)
					continue;
				int transfer = Math.Min(remaining.stack, remaining.maxStack);
				Item placed = remaining.Clone();
				placed.stack = transfer;
				_player.inventory[slot] = placed;
				remaining.stack -= transfer;
				if (remaining.stack <= 0)
					remaining.TurnToAir();
				afterMutation(mutationOrdinal++);
			}
			if (!remaining.IsAir)
				throw new InvalidOperationException(
					$"Vanilla output delivery left ItemType {remaining.type} x{remaining.stack} uninserted.");
		}
		return new ReadOnlyCollection<CraftTransactionItemStack>(outputs.ToArray());
	}

	private bool SimulateConsumption(
		Item[] simulation,
		CraftPreparedSourceAllocation prepared,
		long offset,
		long quantity)
	{
		VanillaInventoryTransactionAdapter.VanillaPreparedAllocationState state =
			VanillaInventoryTransactionAdapter.State(prepared);
		long remaining = quantity;
		long skipped = offset;
		foreach (VanillaInventoryTransactionAdapter.VanillaSlotSegment segment in state.Segments) {
			if (remaining == 0)
				break;
			if (skipped >= segment.ReservedQuantity) {
				skipped -= segment.ReservedQuantity;
				continue;
			}
			long take = Math.Min(remaining, segment.ReservedQuantity - skipped);
			skipped = 0;
			Item item = simulation[segment.Slot];
			if (item.IsAir || item.type != segment.ItemType || item.stack < take)
				return false;
			item.stack -= checked((int)take);
			if (item.stack <= 0)
				item.TurnToAir();
			remaining -= take;
		}
		return remaining == 0;
	}

	private bool TryInsertSimulation(Item[] inventory, Item remaining)
	{
		for (int slot = 0; slot < GeneralSlotCount && !remaining.IsAir; slot++) {
			Item destination = inventory[slot];
			if (destination.IsAir || !ItemLoader.CanStack(destination, remaining))
				continue;
			int transfer = Math.Min(remaining.stack, destination.maxStack - destination.stack);
			if (transfer <= 0)
				continue;
			destination.stack += transfer;
			remaining.stack -= transfer;
			if (remaining.stack <= 0)
				remaining.TurnToAir();
		}
		for (int slot = 0; slot < GeneralSlotCount && !remaining.IsAir; slot++) {
			if (!inventory[slot].IsAir)
				continue;
			int transfer = Math.Min(remaining.stack, remaining.maxStack);
			Item placed = remaining.Clone();
			placed.stack = transfer;
			inventory[slot] = placed;
			remaining.stack -= transfer;
			if (remaining.stack <= 0)
				remaining.TurnToAir();
		}
		return remaining.IsAir;
	}

	private static bool TryGetItem(
		CraftTransactionItemStack output,
		out Item item,
		out string? issue)
	{
		if (output.RuntimePayload is not Item payload || payload.IsAir || payload.type != output.ItemType ||
			output.Quantity <= 0 || output.Quantity > int.MaxValue || payload.stack != output.Quantity) {
			item = new Item();
			issue = $"Output ItemType {output.ItemType} lacks an exact Terraria Item payload.";
			return false;
		}
		item = payload;
		issue = null;
		return true;
	}

	private int GeneralSlotCount => Math.Min(GeneralInventorySlotCount, _player.inventory.Length);
}
