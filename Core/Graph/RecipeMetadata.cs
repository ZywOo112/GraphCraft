using System;
using System.Collections.Generic;

namespace GraphCraft.Core.Graph;

public sealed class RecipeConditionInfo
{
	internal RecipeConditionInfo(
		int conditionIndex,
		string localizationKey,
		string description,
		Func<bool>? evaluator)
	{
		ConditionIndex = conditionIndex;
		LocalizationKey = localizationKey;
		Description = description;
		Evaluator = evaluator;
	}

	public int ConditionIndex { get; }

	public string LocalizationKey { get; }

	public string Description { get; }

	internal Func<bool>? Evaluator { get; }
}

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
