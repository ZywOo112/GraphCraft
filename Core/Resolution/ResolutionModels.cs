using System.Collections.Generic;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Resolution;

public enum ItemResolutionState
{
	Owned,
	UnownedAtomic,
	UnownedDerived
}

public sealed record ItemResolution(
	ItemNode Item,
	ItemResolutionState State,
	int CurrentCount,
	int ComponentId,
	bool IsCyclic);

public enum IngredientResolutionMode
{
	DirectItem,
	RecipeGroup
}

public sealed record MatchedInventoryItem(int ItemType, int Count);

public sealed record IngredientResolution(
	IngredientEdge Ingredient,
	int RequiredAmount,
	int AvailableAmount,
	bool LocallySatisfied,
	IngredientResolutionMode ResolutionMode,
	IReadOnlyList<MatchedInventoryItem> MatchedAlternativeItems);

public enum StationConstraintState
{
	Satisfied,
	Unsatisfied
}

public sealed record StationConstraintResolution(
	StationConstraintState State,
	IReadOnlyList<int> RequiredTileTypes,
	IReadOnlyList<int> MissingTileTypes)
{
	public bool IsSatisfied => State == StationConstraintState.Satisfied;
}

public enum ConditionConstraintState
{
	Satisfied,
	Unsatisfied,
	Unknown
}

public sealed record ConditionConstraintResolution(
	RecipeConditionKey Key,
	string Description,
	ConditionConstraintState State,
	string? UnknownReason);

public enum RecipeLocalState
{
	ReadyNow,
	NeedsIngredients,
	BlockedByStation,
	BlockedByCondition,
	UnknownConstraint
}

public sealed record RecipeResolution(
	RecipeNode Recipe,
	RecipeLocalState State,
	IReadOnlyList<IngredientResolution> Ingredients,
	StationConstraintResolution Station,
	IReadOnlyList<ConditionConstraintResolution> Conditions,
	int ComponentId,
	bool IsCyclic);
