using System.Collections.Generic;

namespace GraphCraft.Core.Graph;

public sealed record RecipeConditionInfo(string LocalizationKey, string Description);

public sealed record RecipeGroupInfo(
	int GroupId,
	int IconicItemType,
	IReadOnlyList<int> AcceptedItemTypes,
	string DisplayText);

public sealed class RecipeConstraintSet
{
	public RecipeConstraintSet(
		IReadOnlyList<int> requiredTileTypes,
		IReadOnlyList<RecipeConditionInfo> conditions)
	{
		RequiredTileTypes = requiredTileTypes;
		Conditions = conditions;
	}

	public IReadOnlyList<int> RequiredTileTypes { get; }

	public IReadOnlyList<RecipeConditionInfo> Conditions { get; }

	public bool HasRequiredTiles => RequiredTileTypes.Count > 0;

	public bool HasConditions => Conditions.Count > 0;
}
