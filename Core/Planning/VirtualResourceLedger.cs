using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Planning;

public sealed record VirtualResourceBalance(
	int ItemType,
	long InitialQuantity,
	long PlannedProductionQuantity,
	long ReservedQuantity,
	long RemainingQuantity);

public sealed record VirtualResourceReservation(
	int ItemType,
	long Quantity,
	long AvailableBefore,
	long RemainingAfter);

public sealed record VirtualReservationAttempt(
	bool Succeeded,
	VirtualResourceLedger Ledger,
	VirtualResourceReservation? Reservation,
	long RequestedQuantity,
	long AvailableQuantity,
	long ShortageQuantity);

public sealed record VirtualResourceProduction(
	int ItemType,
	long Quantity,
	long AvailableBefore,
	long AvailableAfter);

public sealed record VirtualProductionCredit(
	VirtualResourceLedger Ledger,
	VirtualResourceProduction Production);

public sealed class VirtualResourceLedger
{
	private readonly IReadOnlyDictionary<int, long> _plannedProductionCounts;
	private readonly IReadOnlyDictionary<int, long> _reservedCounts;

	private VirtualResourceLedger(
		CraftResourceAggregateSnapshot sourceSnapshot,
		IDictionary<int, long> plannedProductionCounts,
		IDictionary<int, long> reservedCounts)
	{
		SourceSnapshot = sourceSnapshot;
		_plannedProductionCounts = new ReadOnlyDictionary<int, long>(
			new Dictionary<int, long>(plannedProductionCounts));
		_reservedCounts = new ReadOnlyDictionary<int, long>(new Dictionary<int, long>(reservedCounts));
	}

	public CraftResourceAggregateSnapshot SourceSnapshot { get; }

	public IReadOnlyDictionary<int, int> InitialCounts => SourceSnapshot.AggregateCounts;

	public IReadOnlyDictionary<int, long> PlannedProductionCounts => _plannedProductionCounts;

	public IReadOnlyDictionary<int, long> ReservedCounts => _reservedCounts;

	public static VirtualResourceLedger Create(CraftResourceAggregateSnapshot sourceSnapshot)
	{
		ArgumentNullException.ThrowIfNull(sourceSnapshot);
		return new VirtualResourceLedger(
			sourceSnapshot,
			new Dictionary<int, long>(),
			new Dictionary<int, long>());
	}

	public VirtualResourceBalance GetBalance(int itemType)
	{
		if (itemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(itemType), "Resource item types must be positive.");

		long initial = SourceSnapshot.GetAvailableCount(itemType);
		long plannedProduction = _plannedProductionCounts.TryGetValue(itemType, out long produced) ? produced : 0;
		long reserved = _reservedCounts.TryGetValue(itemType, out long quantity) ? quantity : 0;
		return new VirtualResourceBalance(
			itemType,
			initial,
			plannedProduction,
			reserved,
			checked(initial + plannedProduction - reserved));
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

		var nextReserved = new Dictionary<int, long>(_reservedCounts) {
			[itemType] = checked(before.ReservedQuantity + quantity)
		};
		var nextLedger = new VirtualResourceLedger(SourceSnapshot, AsMutable(_plannedProductionCounts), nextReserved);
		var reservation = new VirtualResourceReservation(
			itemType,
			quantity,
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

	public VirtualProductionCredit CreditPlannedProduction(int itemType, long quantity)
	{
		if (itemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(itemType), "Produced item types must be positive.");
		if (quantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(quantity), "Planned production quantities must be positive.");

		VirtualResourceBalance before = GetBalance(itemType);
		var nextProduction = new Dictionary<int, long>(_plannedProductionCounts) {
			[itemType] = checked(before.PlannedProductionQuantity + quantity)
		};
		var nextLedger = new VirtualResourceLedger(SourceSnapshot, nextProduction, AsMutable(_reservedCounts));
		var production = new VirtualResourceProduction(
			itemType,
			quantity,
			before.RemainingQuantity,
			nextLedger.GetBalance(itemType).RemainingQuantity);
		return new VirtualProductionCredit(nextLedger, production);
	}

	private static Dictionary<int, long> AsMutable(IReadOnlyDictionary<int, long> values) => new(values);
}
