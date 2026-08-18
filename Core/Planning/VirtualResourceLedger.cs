using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Planning;

public sealed record VirtualResourceBalance(
	int ItemType,
	int InitialQuantity,
	int ReservedQuantity,
	int RemainingQuantity);

public sealed record VirtualResourceReservation(
	int ItemType,
	int Quantity,
	int AvailableBefore,
	int RemainingAfter);

public sealed record VirtualReservationAttempt(
	bool Succeeded,
	VirtualResourceLedger Ledger,
	VirtualResourceReservation? Reservation,
	long RequestedQuantity,
	int AvailableQuantity,
	long ShortageQuantity);

public sealed class VirtualResourceLedger
{
	private readonly IReadOnlyDictionary<int, int> _reservedCounts;

	private VirtualResourceLedger(
		CraftResourceAggregateSnapshot sourceSnapshot,
		IDictionary<int, int> reservedCounts)
	{
		SourceSnapshot = sourceSnapshot;
		_reservedCounts = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>(reservedCounts));
	}

	public CraftResourceAggregateSnapshot SourceSnapshot { get; }

	public IReadOnlyDictionary<int, int> InitialCounts => SourceSnapshot.AggregateCounts;

	public IReadOnlyDictionary<int, int> ReservedCounts => _reservedCounts;

	public static VirtualResourceLedger Create(CraftResourceAggregateSnapshot sourceSnapshot)
	{
		ArgumentNullException.ThrowIfNull(sourceSnapshot);
		return new VirtualResourceLedger(sourceSnapshot, new Dictionary<int, int>());
	}

	public VirtualResourceBalance GetBalance(int itemType)
	{
		if (itemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(itemType), "Resource item types must be positive.");

		int initial = SourceSnapshot.GetAvailableCount(itemType);
		int reserved = _reservedCounts.TryGetValue(itemType, out int quantity) ? quantity : 0;
		return new VirtualResourceBalance(itemType, initial, reserved, checked(initial - reserved));
	}

	public VirtualReservationAttempt TryReserveExact(int itemType, long quantity)
	{
		if (itemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(itemType), "Resource item types must be positive.");
		if (quantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(quantity), "Reservation quantities must be positive.");

		VirtualResourceBalance before = GetBalance(itemType);
		if (quantity > before.RemainingQuantity) {
			return new VirtualReservationAttempt(
				Succeeded: false,
				Ledger: this,
				Reservation: null,
				RequestedQuantity: quantity,
				AvailableQuantity: before.RemainingQuantity,
				ShortageQuantity: quantity - before.RemainingQuantity);
		}

		int reservedQuantity = checked((int)quantity);
		var nextReserved = new Dictionary<int, int>(_reservedCounts) {
			[itemType] = checked(before.ReservedQuantity + reservedQuantity)
		};
		var nextLedger = new VirtualResourceLedger(SourceSnapshot, nextReserved);
		var reservation = new VirtualResourceReservation(
			itemType,
			reservedQuantity,
			before.RemainingQuantity,
			nextLedger.GetBalance(itemType).RemainingQuantity);
		return new VirtualReservationAttempt(
			Succeeded: true,
			Ledger: nextLedger,
			Reservation: reservation,
			RequestedQuantity: quantity,
			AvailableQuantity: before.RemainingQuantity,
			ShortageQuantity: 0);
	}
}
