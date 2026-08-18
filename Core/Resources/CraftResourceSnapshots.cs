using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GraphCraft.Core.Resources;

public readonly record struct CraftResourceSourceId
{
	public CraftResourceSourceId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("A resource source ID cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }

	public override string ToString() => Value;
}

public readonly record struct CraftResourceSourceKind
{
	public CraftResourceSourceKind(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("A resource source kind cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }

	public static CraftResourceSourceKind Inventory { get; } = new("inventory");

	public static CraftResourceSourceKind StorageNetwork { get; } = new("storage-network");

	public static CraftResourceSourceKind Synthetic { get; } = new("synthetic");

	public override string ToString() => Value;
}

public enum CraftResourceConsumptionCapability
{
	EvidenceOnly,
	AdapterTransactionRequired
}

public sealed record CraftResourceSourceDescriptor
{
	public CraftResourceSourceDescriptor(
		CraftResourceSourceId sourceId,
		CraftResourceSourceKind sourceKind,
		string displayName,
		CraftResourceConsumptionCapability consumptionCapability)
	{
		if (string.IsNullOrWhiteSpace(sourceId.Value))
			throw new ArgumentException("A resource source descriptor requires a non-default source ID.", nameof(sourceId));
		if (string.IsNullOrWhiteSpace(sourceKind.Value))
			throw new ArgumentException("A resource source descriptor requires a non-default source kind.", nameof(sourceKind));
		if (string.IsNullOrWhiteSpace(displayName))
			throw new ArgumentException("A resource source display name cannot be empty.", nameof(displayName));

		SourceId = sourceId;
		SourceKind = sourceKind;
		DisplayName = displayName;
		ConsumptionCapability = consumptionCapability;
	}

	public CraftResourceSourceId SourceId { get; }

	public CraftResourceSourceKind SourceKind { get; }

	public string DisplayName { get; }

	public CraftResourceConsumptionCapability ConsumptionCapability { get; }
}

public interface ICraftResourceSource
{
	CraftResourceSourceDescriptor Descriptor { get; }

	CraftResourceSourceSnapshot Capture();
}

public sealed record CraftResourceEvidence(
	CraftResourceSourceId SourceId,
	CraftResourceSourceKind SourceKind,
	string SourceDisplayName,
	CraftResourceConsumptionCapability ConsumptionCapability,
	int ItemType,
	int Count);

public sealed class CraftResourceSourceSnapshot
{
	public CraftResourceSourceSnapshot(
		CraftResourceSourceDescriptor descriptor,
		IEnumerable<KeyValuePair<int, int>> resourceCounts)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(resourceCounts);

		var countsCopy = new Dictionary<int, int>();
		foreach ((int itemType, int count) in resourceCounts) {
			if (itemType <= 0)
				throw new ArgumentOutOfRangeException(nameof(resourceCounts), "Resource item types must be positive.");
			if (count < 0)
				throw new ArgumentOutOfRangeException(nameof(resourceCounts), "Resource counts cannot be negative.");
			if (count > 0)
				countsCopy[itemType] = checked(countsCopy.GetValueOrDefault(itemType) + count);
		}

		Descriptor = descriptor;
		CapturedAtUtc = DateTimeOffset.UtcNow;
		ResourceCounts = new ReadOnlyDictionary<int, int>(countsCopy);
	}

	public CraftResourceSourceDescriptor Descriptor { get; }

	public DateTimeOffset CapturedAtUtc { get; }

	public IReadOnlyDictionary<int, int> ResourceCounts { get; }

	public int GetCount(int itemType) => ResourceCounts.TryGetValue(itemType, out int count) ? count : 0;
}

public sealed class CraftResourceAggregateSnapshot
{
	private CraftResourceAggregateSnapshot(
		IReadOnlyList<CraftResourceSourceSnapshot> sources,
		IReadOnlyDictionary<int, int> aggregateCounts,
		IReadOnlyDictionary<int, IReadOnlyList<CraftResourceEvidence>> evidenceByItemType)
	{
		Sources = sources;
		AggregateCounts = aggregateCounts;
		EvidenceByItemType = evidenceByItemType;
	}

	public IReadOnlyList<CraftResourceSourceSnapshot> Sources { get; }

	public IReadOnlyDictionary<int, int> AggregateCounts { get; }

	public IReadOnlyDictionary<int, IReadOnlyList<CraftResourceEvidence>> EvidenceByItemType { get; }

	public int TotalResourceItemCount => AggregateCounts.Values.Sum();

	public int GetAvailableCount(int itemType) => AggregateCounts.TryGetValue(itemType, out int count) ? count : 0;

	public IReadOnlyList<CraftResourceEvidence> GetEvidence(int itemType) =>
		EvidenceByItemType.TryGetValue(itemType, out IReadOnlyList<CraftResourceEvidence>? evidence)
			? evidence
			: Array.Empty<CraftResourceEvidence>();

	public static CraftResourceAggregateSnapshot Create(IEnumerable<CraftResourceSourceSnapshot> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);
		CraftResourceSourceSnapshot[] sourceCopies = sources.ToArray();
		if (sourceCopies.Select(source => source.Descriptor.SourceId).Distinct().Count() != sourceCopies.Length)
			throw new InvalidOperationException("Every craft resource source snapshot must have a unique SourceId.");

		var aggregate = new Dictionary<int, int>();
		var evidence = new Dictionary<int, List<CraftResourceEvidence>>();
		foreach (CraftResourceSourceSnapshot source in sourceCopies.OrderBy(source => source.Descriptor.SourceId.Value, StringComparer.Ordinal)) {
			foreach ((int itemType, int count) in source.ResourceCounts.OrderBy(pair => pair.Key)) {
				aggregate[itemType] = checked(aggregate.GetValueOrDefault(itemType) + count);
				if (!evidence.TryGetValue(itemType, out List<CraftResourceEvidence>? itemEvidence)) {
					itemEvidence = new List<CraftResourceEvidence>();
					evidence.Add(itemType, itemEvidence);
				}

				itemEvidence.Add(new CraftResourceEvidence(
					source.Descriptor.SourceId,
					source.Descriptor.SourceKind,
					source.Descriptor.DisplayName,
					source.Descriptor.ConsumptionCapability,
					itemType,
					count));
			}
		}

		var sourceList = new ReadOnlyCollection<CraftResourceSourceSnapshot>(sourceCopies.ToArray());
		var aggregateView = new ReadOnlyDictionary<int, int>(aggregate);
		var evidenceView = new ReadOnlyDictionary<int, IReadOnlyList<CraftResourceEvidence>>(evidence.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<CraftResourceEvidence>)new ReadOnlyCollection<CraftResourceEvidence>(pair.Value.ToArray())));
		return new CraftResourceAggregateSnapshot(sourceList, aggregateView, evidenceView);
	}
}
