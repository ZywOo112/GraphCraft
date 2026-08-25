using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Resources;
using Terraria;

namespace GraphCraft.Systems.Execution;

public sealed class VanillaInventoryTransactionAdapter : ICraftResourceTransactionAdapter
{
	public const int RecipeInventorySlotCount = 58;
	private readonly Player _player;

	public VanillaInventoryTransactionAdapter(Player player)
	{
		_player = player ?? throw new ArgumentNullException(nameof(player));
		Descriptor = new CraftResourceSourceDescriptor(
			new CraftResourceSourceId($"vanilla:player-inventory:{player.whoAmI}"),
			CraftResourceSourceKind.Inventory,
			"Vanilla Player Inventory",
			CraftResourceConsumptionCapability.AdapterTransactionRequired);
	}

	public CraftResourceSourceDescriptor Descriptor { get; }

	public IReadOnlyList<CraftPreparedSourceAllocation> Prepare(
		IReadOnlyList<SourceResourceAllocation> allocations)
	{
		ArgumentNullException.ThrowIfNull(allocations);
		Item[] snapshot = CaptureInventory();
		var remainingBySlot = Enumerable.Range(0, SlotCount)
			.ToDictionary(slot => slot, slot => (long)Math.Max(0, snapshot[slot].stack));
		var prepared = new List<CraftPreparedSourceAllocation>();
		foreach (SourceResourceAllocation allocation in allocations) {
			if (allocation.SourceId != Descriptor.SourceId || allocation.ItemType <= 0 || allocation.Quantity <= 0)
				throw new InvalidOperationException("Vanilla inventory received an invalid or foreign source allocation.");

			long needed = allocation.Quantity;
			var segments = new List<VanillaSlotSegment>();
			for (int slot = 0; slot < SlotCount && needed > 0; slot++) {
				Item item = snapshot[slot];
				if (item.IsAir || item.type != allocation.ItemType)
					continue;
				long take = Math.Min(needed, remainingBySlot[slot]);
				if (take <= 0)
					continue;
				segments.Add(new VanillaSlotSegment(slot, allocation.ItemType, item.stack, take));
				remainingBySlot[slot] -= take;
				needed -= take;
			}
			if (needed != 0)
				throw new InvalidOperationException(
					$"Vanilla inventory is short x{needed} of ItemType {allocation.ItemType} during exact slot resolution.");

			var state = new VanillaPreparedAllocationState(snapshot, segments);
			string display = string.Join(",", segments.Select(segment =>
				$"slot:{segment.Slot}/type:{segment.ItemType}/expected:{segment.ExpectedInitialStack}/reserved:{segment.ReservedQuantity}"));
			prepared.Add(new CraftPreparedSourceAllocation(
				allocation,
				new CraftSourceSlotLocator(display, state)));
		}
		return new ReadOnlyCollection<CraftPreparedSourceAllocation>(prepared);
	}

	public void ValidatePrepared(IReadOnlyList<CraftPreparedSourceAllocation> preparedAllocations)
	{
		ArgumentNullException.ThrowIfNull(preparedAllocations);
		foreach (CraftPreparedSourceAllocation prepared in preparedAllocations) {
			VanillaPreparedAllocationState state = State(prepared);
			foreach (VanillaSlotSegment segment in state.Segments) {
				Item current = _player.inventory[segment.Slot];
				Item expected = state.InitialInventory[segment.Slot];
				if (!SameIdentity(current, expected))
					throw new InvalidOperationException(
						$"Vanilla source-local locator changed before mutation: slot {segment.Slot}.");
			}
		}
	}

	public IReadOnlyList<CraftConsumedResource> Consume(
		CraftPreparedSourceAllocation preparedAllocation,
		long quantity,
		int stepSequence,
		int ingredientOrdinal,
		CraftMutationJournal journal)
	{
		ArgumentNullException.ThrowIfNull(preparedAllocation);
		ArgumentNullException.ThrowIfNull(journal);
		if (quantity < 0 || quantity > preparedAllocation.Allocation.Quantity)
			throw new ArgumentOutOfRangeException(nameof(quantity));
		if (quantity == 0)
			return Array.Empty<CraftConsumedResource>();

		VanillaPreparedAllocationState state = State(preparedAllocation);
		if (checked(state.ConsumedQuantity + quantity) > preparedAllocation.Allocation.Quantity)
			throw new InvalidOperationException("A prepared vanilla allocation cannot exceed its reserved maximum.");
		Item[] before = CaptureInventory();
		journal.Record(new DelegateCraftMutationJournalEntry(
			$"Restore vanilla inventory before consuming {preparedAllocation.Locator.Display}",
			() => RestoreInventory(before)));

		var result = new List<CraftConsumedResource>();
		long remaining = quantity;
		long alreadyConsumed = state.ConsumedQuantity;
		foreach (VanillaSlotSegment segment in state.Segments) {
			if (remaining == 0)
				break;
			if (alreadyConsumed >= segment.ReservedQuantity) {
				alreadyConsumed -= segment.ReservedQuantity;
				continue;
			}
			Item item = _player.inventory[segment.Slot];
			long take = Math.Min(remaining, segment.ReservedQuantity - alreadyConsumed);
			alreadyConsumed = 0;
			if (item.IsAir || item.type != segment.ItemType || item.stack < take)
				throw new InvalidOperationException(
					$"Vanilla inventory slot {segment.Slot} no longer contains the prepared quantity.");

			Item consumed = item.Clone();
			consumed.stack = checked((int)take);
			item.stack -= checked((int)take);
			if (item.stack <= 0)
				item.TurnToAir();
			string locator = $"slot:{segment.Slot}";
			var actual = new ActualResourceConsumption(
				Descriptor.SourceId,
				stepSequence,
				ingredientOrdinal,
				segment.ItemType,
				take,
				locator);
			result.Add(new CraftConsumedResource(
				actual,
				new CraftTransactionItemStack(segment.ItemType, take,
					CraftTransactionOutputKind.ConsumedIngredient, stepSequence, -1, consumed)));
			remaining -= take;
		}
		if (remaining != 0)
			throw new InvalidOperationException($"Prepared vanilla locator was short x{remaining} during mutation.");
		state.ConsumedQuantity += quantity;
		return new ReadOnlyCollection<CraftConsumedResource>(result);
	}

	internal static VanillaPreparedAllocationState State(CraftPreparedSourceAllocation prepared) =>
		prepared.Locator.AdapterState as VanillaPreparedAllocationState
		?? throw new InvalidOperationException("Prepared allocation is not a Vanilla inventory locator.");

	internal Item[] CaptureInventory()
	{
		var snapshot = new Item[SlotCount];
		for (int slot = 0; slot < SlotCount; slot++)
			snapshot[slot] = _player.inventory[slot].Clone();
		return snapshot;
	}

	internal void RestoreInventory(IReadOnlyList<Item> snapshot)
	{
		if (snapshot.Count != SlotCount)
			throw new InvalidOperationException("Vanilla inventory rollback snapshot has the wrong slot count.");
		for (int slot = 0; slot < SlotCount; slot++)
			_player.inventory[slot] = snapshot[slot].Clone();
	}

	private int SlotCount => Math.Min(RecipeInventorySlotCount, _player.inventory.Length);

	private static bool SameIdentity(Item current, Item expected) =>
		current.type == expected.type &&
		current.stack == expected.stack &&
		current.prefix == expected.prefix &&
		current.favorited == expected.favorited &&
		current.ModItem?.GetType() == expected.ModItem?.GetType();

	internal sealed record VanillaSlotSegment(
		int Slot,
		int ItemType,
		int ExpectedInitialStack,
		long ReservedQuantity);

	internal sealed class VanillaPreparedAllocationState
	{
		public VanillaPreparedAllocationState(
			IReadOnlyList<Item> initialInventory,
			IReadOnlyList<VanillaSlotSegment> segments)
		{
			InitialInventory = initialInventory;
			Segments = segments;
		}

		public IReadOnlyList<Item> InitialInventory { get; }
		public IReadOnlyList<VanillaSlotSegment> Segments { get; }
		public long ConsumedQuantity { get; set; }
	}
}
