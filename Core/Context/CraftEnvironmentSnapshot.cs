using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GraphCraft.Core.Context;

public readonly record struct CraftEnvironmentSourceId
{
	public CraftEnvironmentSourceId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("An environment source ID cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }

	public override string ToString() => Value;
}

public sealed record CraftStationEvidence(CraftEnvironmentSourceId SourceId, int TileType);

public interface ICraftEnvironmentSource
{
	CraftEnvironmentSourceId SourceId { get; }

	CraftEnvironmentSnapshot Capture();
}

public sealed class CraftEnvironmentSnapshot
{
	private readonly HashSet<int> _availableTileTypes;

	public CraftEnvironmentSnapshot(IEnumerable<CraftStationEvidence> stationEvidence)
	{
		ArgumentNullException.ThrowIfNull(stationEvidence);
		CraftStationEvidence[] evidenceCopy = stationEvidence.ToArray();
		if (evidenceCopy.Any(evidence => string.IsNullOrWhiteSpace(evidence.SourceId.Value) || evidence.TileType < 0))
			throw new ArgumentException("Station evidence requires a source ID and a non-negative tile type.", nameof(stationEvidence));

		StationEvidence = new ReadOnlyCollection<CraftStationEvidence>(evidenceCopy
			.OrderBy(evidence => evidence.SourceId.Value, StringComparer.Ordinal)
			.ThenBy(evidence => evidence.TileType)
			.ToArray());
		_availableTileTypes = new HashSet<int>(evidenceCopy.Select(evidence => evidence.TileType));
		AvailableTileTypes = new ReadOnlyCollection<int>(_availableTileTypes.OrderBy(tileType => tileType).ToArray());
	}

	public IReadOnlyList<CraftStationEvidence> StationEvidence { get; }

	public IReadOnlyList<int> AvailableTileTypes { get; }

	public bool IsTileAvailable(int tileType) => _availableTileTypes.Contains(tileType);

	public static CraftEnvironmentSnapshot Merge(IEnumerable<CraftEnvironmentSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		return new CraftEnvironmentSnapshot(snapshots.SelectMany(snapshot => snapshot.StationEvidence));
	}

	public static CraftEnvironmentSnapshot CreateSingleSource(
		CraftEnvironmentSourceId sourceId,
		IEnumerable<int> availableTileTypes) =>
		new(availableTileTypes.Select(tileType => new CraftStationEvidence(sourceId, tileType)));
}
