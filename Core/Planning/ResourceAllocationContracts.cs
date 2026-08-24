using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Graph;

namespace GraphCraft.Core.Planning;

public readonly record struct ResourceAllocationDecisionId
{
	public ResourceAllocationDecisionId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Resource allocation decision identity cannot be empty.", nameof(value));
		Value = value;
	}

	public string Value { get; }

	public override string ToString() => Value;
}

public sealed record ConcreteItemAllocation
{
	public ConcreteItemAllocation(int itemType, long quantity)
	{
		if (itemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(itemType));
		if (quantity <= 0)
			throw new ArgumentOutOfRangeException(nameof(quantity));
		ItemType = itemType;
		Quantity = quantity;
	}

	public int ItemType { get; }
	public long Quantity { get; }
}

public sealed class RecipeGroupAllocation
{
	internal RecipeGroupAllocation(
		ResourceAllocationDecisionId decisionId,
		IngredientEdge requirement,
		long requiredQuantity,
		IReadOnlyList<int> acceptedItemTypes,
		IReadOnlyList<ConcreteItemAllocation> concreteAllocations,
		IReadOnlyList<VirtualResourceReservation> reservations)
	{
		DecisionId = decisionId;
		Requirement = requirement;
		RequiredQuantity = requiredQuantity;
		AcceptedItemTypes = new ReadOnlyCollection<int>(acceptedItemTypes.Distinct().OrderBy(itemType => itemType).ToArray());
		ConcreteAllocations = new ReadOnlyCollection<ConcreteItemAllocation>(concreteAllocations
			.OrderBy(allocation => allocation.ItemType)
			.ToArray());
		Reservations = new ReadOnlyCollection<VirtualResourceReservation>(reservations.ToArray());
		if (ConcreteAllocations.Sum(allocation => allocation.Quantity) != requiredQuantity)
			throw new ArgumentException("Concrete Recipe Group allocations must exactly satisfy the requirement.", nameof(concreteAllocations));
		if (ConcreteAllocations.Any(allocation => !AcceptedItemTypes.Contains(allocation.ItemType)))
			throw new ArgumentException("Concrete Recipe Group allocation contains a non-accepted ItemType.", nameof(concreteAllocations));
	}

	public ResourceAllocationDecisionId DecisionId { get; }
	public IngredientEdge Requirement { get; }
	public IReadOnlyList<RecipeGroupInfo> RecipeGroups => Requirement.AcceptedRecipeGroups;
	public long RequiredQuantity { get; }
	public IReadOnlyList<int> AcceptedItemTypes { get; }
	public IReadOnlyList<ConcreteItemAllocation> ConcreteAllocations { get; }
	public IReadOnlyList<VirtualResourceReservation> Reservations { get; }

	public string DeterministicSignature => string.Join(",", ConcreteAllocations.Select(allocation => $"{allocation.ItemType}:{allocation.Quantity}"));
}

public enum ResourceAllocationCandidateStatus
{
	RootSuccessful,
	Failed,
	Deferred,
	SearchBudgetBoundary
}

public sealed record ResourceAllocationCandidateResult(
	RecipeGroupAllocation Allocation,
	ResourceAllocationCandidateStatus Status,
	RecursivePlanStatus ResultStatus,
	PlanningFrontier? Frontier);

public sealed class ResourceAllocationDecision
{
	internal ResourceAllocationDecision(
		ResourceAllocationDecisionId decisionId,
		IngredientEdge requirement,
		long requiredQuantity,
		IReadOnlyList<int> acceptedItemTypes,
		IReadOnlyList<ResourceAllocationCandidateResult> candidates,
		RecipeGroupAllocation? selectedAllocation)
	{
		DecisionId = decisionId;
		Requirement = requirement;
		RequiredQuantity = requiredQuantity;
		AcceptedItemTypes = new ReadOnlyCollection<int>(acceptedItemTypes.Distinct().OrderBy(itemType => itemType).ToArray());
		Candidates = new ReadOnlyCollection<ResourceAllocationCandidateResult>(candidates
			.OrderBy(candidate => candidate.Allocation.DeterministicSignature, StringComparer.Ordinal)
			.ToArray());
		SelectedAllocation = selectedAllocation;
	}

	public ResourceAllocationDecisionId DecisionId { get; }
	public IngredientEdge Requirement { get; }
	public long RequiredQuantity { get; }
	public IReadOnlyList<int> AcceptedItemTypes { get; }
	public IReadOnlyList<ResourceAllocationCandidateResult> Candidates { get; }
	public RecipeGroupAllocation? SelectedAllocation { get; }
	public bool IsResolved => SelectedAllocation is not null;
}

public sealed record PlanningSearchStatistics(
	int ExpandedBranches,
	int MaximumBranchExpansions,
	bool BudgetExhausted,
	int SuccessfulPlanCount,
	int FailedPlanCount);
