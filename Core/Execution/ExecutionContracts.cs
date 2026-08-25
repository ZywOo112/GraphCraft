using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Execution;

public enum ExecutionPreflightStatus
{
	Ready,
	LogicalPlanNotExecutable,
	InvalidLogicalPlan,
	InsufficientExecutableResources,
	UnsupportedSourceCapability,
	SourceUnavailable,
	SourceDescriptorChanged,
	SourceQuantityChanged,
	EnvironmentChanged,
	ConditionChanged
}

public enum ExecutionPreflightIssueKind
{
	LogicalPlanNotSuccessful,
	InvalidLogicalPlan,
	InsufficientExecutableResources,
	EvidenceOnlyResourceRequired,
	SourceUnavailable,
	SourceKindChanged,
	SourceCapabilityChanged,
	SourceQuantityDecreased,
	StationUnsatisfied,
	ConditionUnsatisfied,
	ConditionUnknown
}

public enum ExecutionProductionKind
{
	TransactionLocalIntermediate,
	FinalOutput
}

public sealed record ExecutionResourceDemand(
	int StepSequence,
	int IngredientOrdinal,
	int ItemType,
	long RequiredQuantity,
	long TransactionLocalQuantity,
	long ExternalSourceQuantity,
	bool IsRecipeGroupAllocation)
{
	public long AccountedQuantity => checked(TransactionLocalQuantity + ExternalSourceQuantity);
}

public sealed record ExecutionProduction(
	int ItemType,
	long Quantity,
	ExecutionProductionKind Kind);

public sealed class ExecutionStepPlan
{
	internal ExecutionStepPlan(
		PlannedCraftStep logicalStep,
		IReadOnlyList<ExecutionResourceDemand> demands,
		ExecutionProduction production)
	{
		ArgumentNullException.ThrowIfNull(logicalStep);
		ArgumentNullException.ThrowIfNull(demands);
		ArgumentNullException.ThrowIfNull(production);
		LogicalStep = logicalStep;
		Demands = new ReadOnlyCollection<ExecutionResourceDemand>(demands.ToArray());
		Production = production;
	}

	public PlannedCraftStep LogicalStep { get; }
	public int Sequence => LogicalStep.Sequence;
	public RecipeNode Recipe => LogicalStep.Recipe;
	public long CraftRepetitions => LogicalStep.CraftRepetitions;
	public IReadOnlyList<ExecutionResourceDemand> Demands { get; }
	public ExecutionProduction Production { get; }
}

public sealed record SourceResourceAllocation(
	CraftResourceSourceId SourceId,
	CraftResourceSourceKind SourceKind,
	string SourceDisplayName,
	CraftResourceConsumptionCapability ConsumptionCapability,
	int StepSequence,
	int IngredientOrdinal,
	int ItemType,
	long Quantity);

public sealed class SourceSnapshotExpectation
{
	internal SourceSnapshotExpectation(
		CraftResourceSourceDescriptor expectedDescriptor,
		DateTimeOffset planningCapturedAtUtc,
		IReadOnlyDictionary<int, long> requiredResourceCounts)
	{
		ExpectedDescriptor = expectedDescriptor;
		PlanningCapturedAtUtc = planningCapturedAtUtc;
		RequiredResourceCounts = new ReadOnlyDictionary<int, long>(new Dictionary<int, long>(requiredResourceCounts));
	}

	public CraftResourceSourceDescriptor ExpectedDescriptor { get; }
	public CraftResourceSourceId SourceId => ExpectedDescriptor.SourceId;
	public DateTimeOffset PlanningCapturedAtUtc { get; }
	public IReadOnlyDictionary<int, long> RequiredResourceCounts { get; }
}

public sealed record RecipePreflightEvidence(
	int StepSequence,
	int RecipeIndex,
	StationConstraintState StationState,
	IReadOnlyList<ConditionConstraintResolution> Conditions)
{
	public bool EnvironmentSatisfied => StationState == StationConstraintState.Satisfied;
	public bool ConditionsSatisfied => Conditions.All(condition => condition.State == ConditionConstraintState.Satisfied);
}

public sealed record ExecutionPreflightIssue(
	ExecutionPreflightIssueKind Kind,
	string Message,
	int? StepSequence = null,
	int? RecipeIndex = null,
	CraftResourceSourceId? SourceId = null,
	int? ItemType = null,
	long RequiredQuantity = 0,
	long AvailableQuantity = 0,
	RecipeConditionKey? ConditionKey = null);

public sealed class CraftExecutionPreflightRequest
{
	public CraftExecutionPreflightRequest(
		TargetCraftPlanResult logicalPlan,
		ResolutionSnapshot freshResolution)
	{
		ArgumentNullException.ThrowIfNull(logicalPlan);
		ArgumentNullException.ThrowIfNull(freshResolution);
		LogicalPlan = logicalPlan;
		FreshResolution = freshResolution;
	}

	public TargetCraftPlanResult LogicalPlan { get; }
	public ResolutionSnapshot FreshResolution { get; }
}

public sealed class ExecutableTransactionPlan
{
	internal ExecutableTransactionPlan(
		TargetCraftPlanResult logicalPlan,
		ResolutionSnapshot validatedResolution,
		IReadOnlyList<ExecutionStepPlan> steps,
		IReadOnlyList<SourceResourceAllocation> allocations,
		IReadOnlyList<SourceSnapshotExpectation> sourceExpectations,
		IReadOnlyList<RecipePreflightEvidence> recipeEvidence)
	{
		LogicalPlan = logicalPlan;
		ValidatedResolution = validatedResolution;
		Steps = new ReadOnlyCollection<ExecutionStepPlan>(steps.ToArray());
		Allocations = new ReadOnlyCollection<SourceResourceAllocation>(allocations.ToArray());
		SourceExpectations = new ReadOnlyCollection<SourceSnapshotExpectation>(sourceExpectations.ToArray());
		RecipeEvidence = new ReadOnlyCollection<RecipePreflightEvidence>(recipeEvidence.ToArray());
	}

	public TargetCraftPlanResult LogicalPlan { get; }
	public ResolutionSnapshot ValidatedResolution { get; }
	public IReadOnlyList<ExecutionStepPlan> Steps { get; }
	public IReadOnlyList<SourceResourceAllocation> Allocations { get; }
	public IReadOnlyList<SourceSnapshotExpectation> SourceExpectations { get; }
	public IReadOnlyList<RecipePreflightEvidence> RecipeEvidence { get; }
	public bool MutatesRuntimeState => false;
}

public sealed class CraftExecutionPreflightResult
{
	internal CraftExecutionPreflightResult(
		CraftExecutionPreflightRequest request,
		ExecutionPreflightStatus status,
		IReadOnlyList<ExecutionStepPlan> stepPreview,
		IReadOnlyList<SourceResourceAllocation> allocationPreview,
		IReadOnlyList<SourceSnapshotExpectation> sourceExpectations,
		IReadOnlyList<RecipePreflightEvidence> recipeEvidence,
		IReadOnlyList<ExecutionPreflightIssue> issues,
		ExecutableTransactionPlan? transactionPlan)
	{
		Request = request;
		Status = status;
		StepPreview = new ReadOnlyCollection<ExecutionStepPlan>(stepPreview.ToArray());
		AllocationPreview = new ReadOnlyCollection<SourceResourceAllocation>(allocationPreview.ToArray());
		SourceExpectations = new ReadOnlyCollection<SourceSnapshotExpectation>(sourceExpectations.ToArray());
		RecipeEvidence = new ReadOnlyCollection<RecipePreflightEvidence>(recipeEvidence.ToArray());
		Issues = new ReadOnlyCollection<ExecutionPreflightIssue>(issues.ToArray());
		TransactionPlan = transactionPlan;
	}

	public CraftExecutionPreflightRequest Request { get; }
	public TargetCraftPlanResult LogicalPlan => Request.LogicalPlan;
	public ResolutionSnapshot FreshResolution => Request.FreshResolution;
	public ExecutionPreflightStatus Status { get; }
	public bool Succeeded => Status == ExecutionPreflightStatus.Ready && TransactionPlan is not null;
	public bool MutatesRuntimeState => false;
	public IReadOnlyList<ExecutionStepPlan> StepPreview { get; }
	public IReadOnlyList<SourceResourceAllocation> AllocationPreview { get; }
	public IReadOnlyList<SourceSnapshotExpectation> SourceExpectations { get; }
	public IReadOnlyList<RecipePreflightEvidence> RecipeEvidence { get; }
	public IReadOnlyList<ExecutionPreflightIssue> Issues { get; }
	public ExecutableTransactionPlan? TransactionPlan { get; }
}
