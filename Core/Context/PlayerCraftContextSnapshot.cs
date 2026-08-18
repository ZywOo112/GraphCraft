using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GraphCraft.Core.Resources;

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

	private PlayerCraftContextSnapshot(
		IEnumerable<CraftResourceSourceSnapshot> resourceSources,
		CraftEnvironmentSnapshot environment,
		IEnumerable<CapturedConditionEvidence> conditionEvidence,
		int? sourcePlayerIndex,
		bool isSynthetic,
		TimeSpan? captureElapsed,
		long? captureStartTimestamp)
	{
		var creationTimer = Stopwatch.StartNew();
		ArgumentNullException.ThrowIfNull(resourceSources);
		ArgumentNullException.ThrowIfNull(environment);
		var conditionsCopy = conditionEvidence.ToDictionary(evidence => evidence.Key);

		SnapshotId = Interlocked.Increment(ref _nextSnapshotId);
		CapturedAtUtc = DateTimeOffset.UtcNow;
		Resources = CraftResourceAggregateSnapshot.Create(resourceSources);
		Environment = environment;
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

	public CraftResourceAggregateSnapshot Resources { get; }

	public CraftEnvironmentSnapshot Environment { get; }

	public IReadOnlyDictionary<int, int> AggregateResourceCounts => Resources.AggregateCounts;

	public IReadOnlyList<CraftResourceSourceSnapshot> ResourceSources => Resources.Sources;

	public IReadOnlyList<int> AvailableTileTypes => Environment.AvailableTileTypes;

	public IReadOnlyDictionary<RecipeConditionKey, CapturedConditionEvidence> ConditionEvidence { get; }

	public int? SourcePlayerIndex { get; }

	public bool IsSynthetic { get; }

	public TimeSpan CaptureElapsed { get; }

	public int TotalResourceItemCount => Resources.TotalResourceItemCount;

	public int GetAvailableCount(int itemType) => Resources.GetAvailableCount(itemType);

	public IReadOnlyList<CraftResourceEvidence> GetResourceEvidence(int itemType) => Resources.GetEvidence(itemType);

	public bool IsTileAvailable(int tileType) => Environment.IsTileAvailable(tileType);

	public bool TryGetConditionEvidence(RecipeConditionKey key, out CapturedConditionEvidence? evidence) =>
		ConditionEvidence.TryGetValue(key, out evidence);

	public static PlayerCraftContextSnapshot CreateSynthetic(
		IEnumerable<KeyValuePair<int, int>> resourceCounts,
		IEnumerable<int>? availableTileTypes = null,
		IEnumerable<CapturedConditionEvidence>? conditionEvidence = null,
		TimeSpan? captureElapsed = null)
	{
		ArgumentNullException.ThrowIfNull(resourceCounts);
		var descriptor = new CraftResourceSourceDescriptor(
			new CraftResourceSourceId("synthetic:default"),
			CraftResourceSourceKind.Synthetic,
			"Synthetic Default Source",
			CraftResourceConsumptionCapability.EvidenceOnly);
		return new PlayerCraftContextSnapshot(
			new[] { new CraftResourceSourceSnapshot(descriptor, resourceCounts) },
			CraftEnvironmentSnapshot.CreateSingleSource(
				new CraftEnvironmentSourceId("synthetic:stations"),
				availableTileTypes ?? Array.Empty<int>()),
			conditionEvidence ?? Array.Empty<CapturedConditionEvidence>(),
			sourcePlayerIndex: null,
			isSynthetic: true,
			captureElapsed,
			captureStartTimestamp: null);
	}

	public static PlayerCraftContextSnapshot CreateSynthetic(
		IEnumerable<CraftResourceSourceSnapshot> resourceSources,
		CraftEnvironmentSnapshot? environment = null,
		IEnumerable<CapturedConditionEvidence>? conditionEvidence = null,
		TimeSpan? captureElapsed = null)
	{
		ArgumentNullException.ThrowIfNull(resourceSources);
		return new PlayerCraftContextSnapshot(
			resourceSources,
			environment ?? new CraftEnvironmentSnapshot(Array.Empty<CraftStationEvidence>()),
			conditionEvidence ?? Array.Empty<CapturedConditionEvidence>(),
			sourcePlayerIndex: null,
			isSynthetic: true,
			captureElapsed,
			captureStartTimestamp: null);
	}

	internal static PlayerCraftContextSnapshot CreateCaptured(
		IEnumerable<CraftResourceSourceSnapshot> resourceSources,
		CraftEnvironmentSnapshot environment,
		IEnumerable<CapturedConditionEvidence> conditionEvidence,
		int sourcePlayerIndex,
		long captureStartTimestamp) =>
		new(
			resourceSources,
			environment,
			conditionEvidence,
			sourcePlayerIndex,
			isSynthetic: false,
			captureElapsed: null,
			captureStartTimestamp);
}
