using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GraphCraft.Core.Context;

public readonly record struct RecipeConditionKey(int RecipeIndex, int ConditionIndex)
{
	public override string ToString() => $"recipe:{RecipeIndex}:condition:{ConditionIndex}";
}

public enum CapturedConditionState
{
	Satisfied,
	Unsatisfied,
	Unknown
}

public sealed record CapturedConditionEvidence(
	RecipeConditionKey Key,
	string Description,
	CapturedConditionState State,
	string? UnknownReason = null);

public sealed class PlayerCraftContextSnapshot
{
	private static long _nextSnapshotId;
	private readonly HashSet<int> _availableTileTypes;

	private PlayerCraftContextSnapshot(
		IEnumerable<KeyValuePair<int, int>> inventoryCounts,
		IEnumerable<int> availableTileTypes,
		IEnumerable<CapturedConditionEvidence> conditionEvidence,
		int? sourcePlayerIndex,
		bool isSynthetic,
		TimeSpan? captureElapsed,
		long? captureStartTimestamp)
	{
		var creationTimer = Stopwatch.StartNew();
		var inventoryCopy = new Dictionary<int, int>();
		foreach ((int itemType, int count) in inventoryCounts) {
			if (itemType <= 0)
				throw new ArgumentOutOfRangeException(nameof(inventoryCounts), "Inventory item types must be positive.");
			if (count < 0)
				throw new ArgumentOutOfRangeException(nameof(inventoryCounts), "Inventory counts cannot be negative.");
			if (count > 0)
				inventoryCopy[itemType] = checked(inventoryCopy.GetValueOrDefault(itemType) + count);
		}

		_availableTileTypes = new HashSet<int>(availableTileTypes.Where(tileType => tileType >= 0));
		var conditionsCopy = conditionEvidence.ToDictionary(evidence => evidence.Key);

		SnapshotId = Interlocked.Increment(ref _nextSnapshotId);
		CapturedAtUtc = DateTimeOffset.UtcNow;
		InventoryCounts = new ReadOnlyDictionary<int, int>(inventoryCopy);
		AvailableTileTypes = new ReadOnlyCollection<int>(_availableTileTypes.OrderBy(tileType => tileType).ToArray());
		ConditionEvidence = new ReadOnlyDictionary<RecipeConditionKey, CapturedConditionEvidence>(conditionsCopy);
		SourcePlayerIndex = sourcePlayerIndex;
		IsSynthetic = isSynthetic;
		creationTimer.Stop();
		CaptureElapsed = captureStartTimestamp is long started
			? Stopwatch.GetElapsedTime(started)
			: captureElapsed ?? creationTimer.Elapsed;
	}

	public long SnapshotId { get; }

	public DateTimeOffset CapturedAtUtc { get; }

	public IReadOnlyDictionary<int, int> InventoryCounts { get; }

	public IReadOnlyList<int> AvailableTileTypes { get; }

	public IReadOnlyDictionary<RecipeConditionKey, CapturedConditionEvidence> ConditionEvidence { get; }

	public int? SourcePlayerIndex { get; }

	public bool IsSynthetic { get; }

	public TimeSpan CaptureElapsed { get; }

	public int TotalInventoryItemCount => InventoryCounts.Values.Sum();

	public int GetInventoryCount(int itemType) => InventoryCounts.TryGetValue(itemType, out int count) ? count : 0;

	public bool IsTileAvailable(int tileType) => _availableTileTypes.Contains(tileType);

	public bool TryGetConditionEvidence(RecipeConditionKey key, out CapturedConditionEvidence? evidence) =>
		ConditionEvidence.TryGetValue(key, out evidence);

	public static PlayerCraftContextSnapshot CreateSynthetic(
		IEnumerable<KeyValuePair<int, int>> inventoryCounts,
		IEnumerable<int>? availableTileTypes = null,
		IEnumerable<CapturedConditionEvidence>? conditionEvidence = null,
		TimeSpan? captureElapsed = null)
	{
		ArgumentNullException.ThrowIfNull(inventoryCounts);
		return new PlayerCraftContextSnapshot(
			inventoryCounts,
			availableTileTypes ?? Array.Empty<int>(),
			conditionEvidence ?? Array.Empty<CapturedConditionEvidence>(),
			sourcePlayerIndex: null,
			isSynthetic: true,
			captureElapsed,
			captureStartTimestamp: null);
	}

	internal static PlayerCraftContextSnapshot CreateCaptured(
		IEnumerable<KeyValuePair<int, int>> inventoryCounts,
		IEnumerable<int> availableTileTypes,
		IEnumerable<CapturedConditionEvidence> conditionEvidence,
		int sourcePlayerIndex,
		long captureStartTimestamp) =>
		new(
			inventoryCounts,
			availableTileTypes,
			conditionEvidence,
			sourcePlayerIndex,
			isSynthetic: false,
			captureElapsed: null,
			captureStartTimestamp);
}
