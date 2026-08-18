using System;
using System.Collections.Generic;
using GraphCraft.Core.Context;
using Terraria;

namespace GraphCraft.Systems.Adapters;

public sealed class VanillaPlayerEnvironmentSource : ICraftEnvironmentSource
{
	private readonly Player _player;

	public VanillaPlayerEnvironmentSource(Player player)
	{
		_player = player ?? throw new ArgumentNullException(nameof(player));
		SourceId = new CraftEnvironmentSourceId($"vanilla:adjacent-tiles:{player.whoAmI}");
	}

	public CraftEnvironmentSourceId SourceId { get; }

	public CraftEnvironmentSnapshot Capture()
	{
		bool[] adjacentTiles = _player.adjTile;
		var availableTileTypes = new List<int>();
		for (int tileType = 0; tileType < adjacentTiles.Length; tileType++) {
			if (adjacentTiles[tileType])
				availableTileTypes.Add(tileType);
		}

		return CraftEnvironmentSnapshot.CreateSingleSource(SourceId, availableTileTypes);
	}
}
