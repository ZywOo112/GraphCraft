using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Core.Planning;

public enum SingleRecipePlanStatus
{
	Success,
	RecipeOutsideTargetSubgraph,
	BlockedByStation,
	BlockedByCondition,
	UnknownConstraint,
	InsufficientDirectResources,
	DeferredRecipeGroupAllocation
}

public enum IngredientPlanStatus
{
	Reserved,
	Insufficient,
	DeferredRecipeGroup,
	NotAttempted,
	StagedThenDiscarded
}

public sealed record IngredientPlanResult(
	IngredientEdge Ingredient,
	IngredientPlanStatus Status,
	long RequiredQuantity,
	int AvailableBefore,
	int ReservedQuantity,
	int RemainingAfter,
	long ShortageQuantity);

public sealed class SingleRecipePlanningRequest
{
	public SingleRecipePlanningRequest(
		TargetCraftSubgraph targetSubgraph,
		ResolutionSnapshot resolution,
		RecipeNode selectedRecipe,
		int requiredOutputQuantity,
		VirtualResourceLedger ledger)
	{
		ArgumentNullException.ThrowIfNull(targetSubgraph);
		ArgumentNullException.ThrowIfNull(resolution);
		ArgumentNullException.ThrowIfNull(selectedRecipe);
		ArgumentNullException.ThrowIfNull(ledger);
		if (requiredOutputQuantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(requiredOutputQuantity), "Required output quantity must be positive.");
		if (!ReferenceEquals(targetSubgraph, resolution.SourceSubgraph))
			throw new ArgumentException("The resolution snapshot must belong to the requested target subgraph.", nameof(resolution));
		if (!ReferenceEquals(ledger.SourceSnapshot, resolution.Context.Resources))
			throw new ArgumentException("The virtual ledger must branch from the resource snapshot used by the resolution.", nameof(ledger));

		TargetSubgraph = targetSubgraph;
		Resolution = resolution;
		SelectedRecipe = selectedRecipe;
		RequiredOutputQuantity = requiredOutputQuantity;
		Ledger = ledger;
	}

	public TargetCraftSubgraph TargetSubgraph { get; }

	public ResolutionSnapshot Resolution { get; }

	public RecipeNode SelectedRecipe { get; }

	public int RequiredOutputQuantity { get; }

	public VirtualResourceLedger Ledger { get; }
}

public sealed class SingleRecipePlanResult
{
	internal SingleRecipePlanResult(
		RecipeNode selectedRecipe,
		SingleRecipePlanStatus status,
		int requestedOutputQuantity,
		long craftRepetitions,
		long producedQuantity,
		IReadOnlyList<IngredientPlanResult> ingredientResults,
		IReadOnlyList<VirtualResourceReservation> reservations,
		VirtualResourceLedger inputLedger,
		VirtualResourceLedger resultLedger,
		RecipeResolution? resolutionEvidence)
	{
		SelectedRecipe = selectedRecipe;
		Status = status;
		RequestedOutputQuantity = requestedOutputQuantity;
		CraftRepetitions = craftRepetitions;
		ProducedQuantity = producedQuantity;
		IngredientResults = new ReadOnlyCollection<IngredientPlanResult>(ingredientResults.ToArray());
		Reservations = new ReadOnlyCollection<VirtualResourceReservation>(reservations.ToArray());
		InputLedger = inputLedger;
		ResultLedger = resultLedger;
		ResolutionEvidence = resolutionEvidence;
	}

	public RecipeNode SelectedRecipe { get; }

	public SingleRecipePlanStatus Status { get; }

	public bool Succeeded => Status == SingleRecipePlanStatus.Success;

	public int RequestedOutputQuantity { get; }

	public long CraftRepetitions { get; }

	public long ProducedQuantity { get; }

	public IReadOnlyList<IngredientPlanResult> IngredientResults { get; }

	public IReadOnlyList<VirtualResourceReservation> Reservations { get; }

	public VirtualResourceLedger InputLedger { get; }

	public VirtualResourceLedger ResultLedger { get; }

	public RecipeResolution? ResolutionEvidence { get; }
}
