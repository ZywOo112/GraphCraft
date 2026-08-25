using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Execution;

/// <summary>
/// Replays one successful logical plan into an immutable, SourceId-bound
/// transaction plan. The planner consumes snapshots only and never mutates,
/// reserves, crafts, commits, rolls back, or changes the selected route.
/// </summary>
public static class CraftExecutionPlanner
{
	public static CraftExecutionPreflightResult CreatePreflight(CraftExecutionPreflightRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		TargetCraftPlanResult logicalPlan = request.LogicalPlan;
		if (!logicalPlan.Succeeded || logicalPlan.SelectedRootRecipe is null || logicalPlan.Steps.Count == 0) {
			return Failure(
				request,
				ExecutionPreflightStatus.LogicalPlanNotExecutable,
				Array.Empty<ExecutionStepPlan>(),
				new ExecutionPreflightIssue(
					ExecutionPreflightIssueKind.LogicalPlanNotSuccessful,
					$"Only a successful, fully resolved logical plan can enter execution planning; status was {logicalPlan.Status}."));
		}

		if (!ReferenceEquals(request.FreshResolution.SourceSubgraph, logicalPlan.Request.TargetSubgraph)) {
			return Failure(
				request,
				ExecutionPreflightStatus.InvalidLogicalPlan,
				Array.Empty<ExecutionStepPlan>(),
				Invalid("The fresh ResolutionSnapshot does not belong to the logical plan's exact TargetCraftSubgraph."));
		}

		if (!TryBuildStepPlans(logicalPlan, out IReadOnlyList<ExecutionStepPlan> steps, out ExecutionPreflightIssue? stepIssue)) {
			return Failure(
				request,
				ExecutionPreflightStatus.InvalidLogicalPlan,
				Array.Empty<ExecutionStepPlan>(),
				stepIssue ?? Invalid("The logical plan could not be converted to an execution step sequence."));
		}

		AllocationAttempt allocation = AllocateOriginalSources(logicalPlan.InputLedger.SourceSnapshot, steps);
		if (!allocation.Succeeded) {
			return new CraftExecutionPreflightResult(
				request,
				allocation.FailureStatus,
				steps,
				Array.Empty<SourceResourceAllocation>(),
				Array.Empty<SourceSnapshotExpectation>(),
				Array.Empty<RecipePreflightEvidence>(),
				allocation.Issues,
				transactionPlan: null);
		}

		IReadOnlyList<ExecutionPreflightIssue> sourceIssues = RevalidateSources(
			request.FreshResolution.Context.Resources,
			allocation.Expectations);
		(IReadOnlyList<RecipePreflightEvidence> recipeEvidence, IReadOnlyList<ExecutionPreflightIssue> constraintIssues) =
			RevalidateRecipeConstraints(request.FreshResolution, steps);
		ExecutionPreflightIssue[] issues = sourceIssues.Concat(constraintIssues).ToArray();
		if (issues.Length > 0) {
			return new CraftExecutionPreflightResult(
				request,
				StatusFor(issues),
				steps,
				allocation.Allocations,
				allocation.Expectations,
				recipeEvidence,
				issues,
				transactionPlan: null);
		}

		var transactionPlan = new ExecutableTransactionPlan(
			logicalPlan,
			request.FreshResolution,
			steps,
			allocation.Allocations,
			allocation.Expectations,
			recipeEvidence);
		return new CraftExecutionPreflightResult(
			request,
			ExecutionPreflightStatus.Ready,
			steps,
			allocation.Allocations,
			allocation.Expectations,
			recipeEvidence,
			Array.Empty<ExecutionPreflightIssue>(),
			transactionPlan);
	}

	private static bool TryBuildStepPlans(
		TargetCraftPlanResult logicalPlan,
		out IReadOnlyList<ExecutionStepPlan> steps,
		out ExecutionPreflightIssue? issue)
	{
		steps = Array.Empty<ExecutionStepPlan>();
		issue = null;
		PlannedCraftStep[] logicalSteps = logicalPlan.Steps.OrderBy(step => step.Sequence).ToArray();
		if (!logicalSteps.Select(step => step.Sequence).SequenceEqual(Enumerable.Range(0, logicalSteps.Length))) {
			issue = Invalid("Logical craft steps are not a contiguous deterministic sequence.");
			return false;
		}
		if (!ReferenceEquals(logicalSteps[^1].Recipe, logicalPlan.SelectedRootRecipe)) {
			issue = Invalid("The final logical craft step is not the selected root recipe.");
			return false;
		}

		var localBuffer = new Dictionary<int, long>();
		var result = new List<ExecutionStepPlan>();
		foreach (PlannedCraftStep logicalStep in logicalSteps) {
			var demands = new List<ExecutionResourceDemand>();
			foreach (PlannedIngredientUse ingredient in logicalStep.Ingredients.OrderBy(use => use.Ingredient.Ordinal)) {
				if (ingredient.RequiredQuantity <= 0) {
					issue = Invalid($"Recipe #{logicalStep.Recipe.RecipeIndex} has a non-positive ingredient demand.");
					return false;
				}

				IReadOnlyList<ConcreteItemAllocation> concrete = ingredient.GroupAllocation is null
					? new[] { new ConcreteItemAllocation(ingredient.Ingredient.Source.ItemType, ingredient.RequiredQuantity) }
					: ingredient.GroupAllocation.ConcreteAllocations;
				if (concrete.Sum(allocation => allocation.Quantity) != ingredient.RequiredQuantity) {
					issue = Invalid($"Recipe #{logicalStep.Recipe.RecipeIndex} ingredient #{ingredient.Ingredient.Ordinal} does not preserve its concrete allocation quantity.");
					return false;
				}

				foreach (ConcreteItemAllocation concreteItem in concrete.OrderBy(allocation => allocation.ItemType)) {
					long availableLocal = localBuffer.GetValueOrDefault(concreteItem.ItemType);
					long localQuantity = Math.Min(concreteItem.Quantity, availableLocal);
					long externalQuantity = concreteItem.Quantity - localQuantity;
					if (localQuantity > 0)
						localBuffer[concreteItem.ItemType] = availableLocal - localQuantity;
					demands.Add(new ExecutionResourceDemand(
						logicalStep.Sequence,
						ingredient.Ingredient.Ordinal,
						concreteItem.ItemType,
						concreteItem.Quantity,
						localQuantity,
						externalQuantity,
						ingredient.GroupAllocation is not null));
				}
			}

			bool finalOutput = ReferenceEquals(logicalStep, logicalSteps[^1]);
			var production = new ExecutionProduction(
				logicalStep.Recipe.Result.ItemType,
				logicalStep.ProducedQuantity,
				finalOutput ? ExecutionProductionKind.FinalOutput : ExecutionProductionKind.TransactionLocalIntermediate);
			localBuffer[production.ItemType] = checked(localBuffer.GetValueOrDefault(production.ItemType) + production.Quantity);
			result.Add(new ExecutionStepPlan(logicalStep, demands, production));
		}

		steps = new ReadOnlyCollection<ExecutionStepPlan>(result);
		return true;
	}

	private static AllocationAttempt AllocateOriginalSources(
		CraftResourceAggregateSnapshot planningSnapshot,
		IReadOnlyList<ExecutionStepPlan> steps)
	{
		var remaining = planningSnapshot.Sources
			.SelectMany(source => source.ResourceCounts.Select(pair => new {
				Key = (source.Descriptor.SourceId, ItemType: pair.Key),
				Count = (long)pair.Value
			}))
			.ToDictionary(entry => entry.Key, entry => entry.Count);
		var allocations = new List<SourceResourceAllocation>();
		foreach (ExecutionResourceDemand demand in steps.SelectMany(step => step.Demands)
			.Where(demand => demand.ExternalSourceQuantity > 0)
			.OrderBy(demand => demand.StepSequence)
			.ThenBy(demand => demand.IngredientOrdinal)
			.ThenBy(demand => demand.ItemType)) {
			long needed = demand.ExternalSourceQuantity;
			CraftResourceEvidence[] evidence = planningSnapshot.GetEvidence(demand.ItemType)
				.OrderBy(item => item.SourceId.Value, StringComparer.Ordinal)
				.ToArray();
			foreach (CraftResourceEvidence item in evidence.Where(item =>
				item.ConsumptionCapability == CraftResourceConsumptionCapability.AdapterTransactionRequired)) {
				var key = (item.SourceId, demand.ItemType);
				long available = remaining.GetValueOrDefault(key);
				long quantity = Math.Min(needed, available);
				if (quantity <= 0)
					continue;
				allocations.Add(new SourceResourceAllocation(
					item.SourceId,
					item.SourceKind,
					item.SourceDisplayName,
					item.ConsumptionCapability,
					demand.StepSequence,
					demand.IngredientOrdinal,
					demand.ItemType,
					quantity));
				remaining[key] = available - quantity;
				needed -= quantity;
				if (needed == 0)
					break;
			}

			if (needed > 0) {
				long evidenceOnlyAvailable = evidence
					.Where(item => item.ConsumptionCapability == CraftResourceConsumptionCapability.EvidenceOnly)
					.Sum(item => remaining.GetValueOrDefault((item.SourceId, demand.ItemType)));
				bool capabilityBlocked = evidenceOnlyAvailable > 0;
				return AllocationAttempt.Failure(
					capabilityBlocked ? ExecutionPreflightStatus.UnsupportedSourceCapability : ExecutionPreflightStatus.InsufficientExecutableResources,
					new ExecutionPreflightIssue(
						capabilityBlocked ? ExecutionPreflightIssueKind.EvidenceOnlyResourceRequired : ExecutionPreflightIssueKind.InsufficientExecutableResources,
						capabilityBlocked
							? $"ItemType {demand.ItemType} would require EvidenceOnly resources; those sources are never allocatable."
							: $"ItemType {demand.ItemType} lacks {needed} allocatable source resources.",
						demand.StepSequence,
						ItemType: demand.ItemType,
						RequiredQuantity: demand.ExternalSourceQuantity,
						AvailableQuantity: demand.ExternalSourceQuantity - needed));
			}
		}

		SourceSnapshotExpectation[] expectations = allocations
			.GroupBy(allocation => allocation.SourceId)
			.OrderBy(group => group.Key.Value, StringComparer.Ordinal)
			.Select(group => {
				CraftResourceSourceSnapshot source = planningSnapshot.Sources.Single(snapshot => snapshot.Descriptor.SourceId == group.Key);
				Dictionary<int, long> required = group
					.GroupBy(allocation => allocation.ItemType)
					.OrderBy(items => items.Key)
					.ToDictionary(items => items.Key, items => items.Sum(allocation => allocation.Quantity));
				return new SourceSnapshotExpectation(source.Descriptor, source.CapturedAtUtc, required);
			})
			.ToArray();
		return AllocationAttempt.Success(allocations, expectations);
	}

	private static IReadOnlyList<ExecutionPreflightIssue> RevalidateSources(
		CraftResourceAggregateSnapshot freshResources,
		IReadOnlyList<SourceSnapshotExpectation> expectations)
	{
		var freshById = freshResources.Sources.ToDictionary(source => source.Descriptor.SourceId);
		var issues = new List<ExecutionPreflightIssue>();
		foreach (SourceSnapshotExpectation expectation in expectations) {
			if (!freshById.TryGetValue(expectation.SourceId, out CraftResourceSourceSnapshot? fresh)) {
				issues.Add(new ExecutionPreflightIssue(
					ExecutionPreflightIssueKind.SourceUnavailable,
					$"Allocated source {expectation.SourceId} no longer exists in the fresh resource snapshot.",
					SourceId: expectation.SourceId));
				continue;
			}

			CraftResourceSourceDescriptor expected = expectation.ExpectedDescriptor;
			if (fresh.Descriptor.SourceKind != expected.SourceKind) {
				issues.Add(new ExecutionPreflightIssue(
					ExecutionPreflightIssueKind.SourceKindChanged,
					$"Allocated source {expectation.SourceId} changed SourceKind from {expected.SourceKind} to {fresh.Descriptor.SourceKind}.",
					SourceId: expectation.SourceId));
				continue;
			}
			if (fresh.Descriptor.ConsumptionCapability != expected.ConsumptionCapability ||
				fresh.Descriptor.ConsumptionCapability != CraftResourceConsumptionCapability.AdapterTransactionRequired) {
				issues.Add(new ExecutionPreflightIssue(
					ExecutionPreflightIssueKind.SourceCapabilityChanged,
					$"Allocated source {expectation.SourceId} is no longer transaction-capable.",
					SourceId: expectation.SourceId));
				continue;
			}

			foreach ((int itemType, long required) in expectation.RequiredResourceCounts) {
				long available = fresh.GetCount(itemType);
				if (available < required) {
					issues.Add(new ExecutionPreflightIssue(
						ExecutionPreflightIssueKind.SourceQuantityDecreased,
						$"Allocated source {expectation.SourceId} now has ItemType {itemType} x{available}, below required x{required}.",
						SourceId: expectation.SourceId,
						ItemType: itemType,
						RequiredQuantity: required,
						AvailableQuantity: available));
				}
			}
		}
		return new ReadOnlyCollection<ExecutionPreflightIssue>(issues);
	}

	private static (IReadOnlyList<RecipePreflightEvidence> Evidence, IReadOnlyList<ExecutionPreflightIssue> Issues)
		RevalidateRecipeConstraints(ResolutionSnapshot freshResolution, IReadOnlyList<ExecutionStepPlan> steps)
	{
		var evidence = new List<RecipePreflightEvidence>();
		var issues = new List<ExecutionPreflightIssue>();
		foreach (ExecutionStepPlan step in steps) {
			RecipeResolution resolution = freshResolution.GetRecipeResolution(step.Recipe);
			evidence.Add(new RecipePreflightEvidence(
				step.Sequence,
				step.Recipe.RecipeIndex,
				resolution.Station.State,
				new ReadOnlyCollection<ConditionConstraintResolution>(resolution.Conditions.ToArray())));
			if (!resolution.Station.IsSatisfied) {
				issues.Add(new ExecutionPreflightIssue(
					ExecutionPreflightIssueKind.StationUnsatisfied,
					$"Recipe #{step.Recipe.RecipeIndex} is missing station tiles [{string.Join(',', resolution.Station.MissingTileTypes)}] in the fresh environment.",
					step.Sequence,
					step.Recipe.RecipeIndex));
			}
			foreach (ConditionConstraintResolution condition in resolution.Conditions) {
				if (condition.State == ConditionConstraintState.Satisfied)
					continue;
				bool unknown = condition.State == ConditionConstraintState.Unknown;
				issues.Add(new ExecutionPreflightIssue(
					unknown ? ExecutionPreflightIssueKind.ConditionUnknown : ExecutionPreflightIssueKind.ConditionUnsatisfied,
					$"Recipe #{step.Recipe.RecipeIndex} condition {condition.Key} is {condition.State}: {condition.Description}.",
					step.Sequence,
					step.Recipe.RecipeIndex,
					ConditionKey: condition.Key));
			}
		}
		return (
			new ReadOnlyCollection<RecipePreflightEvidence>(evidence),
			new ReadOnlyCollection<ExecutionPreflightIssue>(issues));
	}

	private static ExecutionPreflightStatus StatusFor(IReadOnlyList<ExecutionPreflightIssue> issues)
	{
		if (issues.Any(issue => issue.Kind == ExecutionPreflightIssueKind.SourceUnavailable))
			return ExecutionPreflightStatus.SourceUnavailable;
		if (issues.Any(issue => issue.Kind is ExecutionPreflightIssueKind.SourceKindChanged or ExecutionPreflightIssueKind.SourceCapabilityChanged))
			return ExecutionPreflightStatus.SourceDescriptorChanged;
		if (issues.Any(issue => issue.Kind == ExecutionPreflightIssueKind.SourceQuantityDecreased))
			return ExecutionPreflightStatus.SourceQuantityChanged;
		if (issues.Any(issue => issue.Kind == ExecutionPreflightIssueKind.StationUnsatisfied))
			return ExecutionPreflightStatus.EnvironmentChanged;
		return ExecutionPreflightStatus.ConditionChanged;
	}

	private static ExecutionPreflightIssue Invalid(string message) =>
		new(ExecutionPreflightIssueKind.InvalidLogicalPlan, message);

	private static CraftExecutionPreflightResult Failure(
		CraftExecutionPreflightRequest request,
		ExecutionPreflightStatus status,
		IReadOnlyList<ExecutionStepPlan> steps,
		params ExecutionPreflightIssue[] issues) =>
		new(
			request,
			status,
			steps,
			Array.Empty<SourceResourceAllocation>(),
			Array.Empty<SourceSnapshotExpectation>(),
			Array.Empty<RecipePreflightEvidence>(),
			issues,
			transactionPlan: null);

	private sealed class AllocationAttempt
	{
		private AllocationAttempt(
			bool succeeded,
			ExecutionPreflightStatus failureStatus,
			IReadOnlyList<SourceResourceAllocation> allocations,
			IReadOnlyList<SourceSnapshotExpectation> expectations,
			IReadOnlyList<ExecutionPreflightIssue> issues)
		{
			Succeeded = succeeded;
			FailureStatus = failureStatus;
			Allocations = allocations;
			Expectations = expectations;
			Issues = issues;
		}

		public bool Succeeded { get; }
		public ExecutionPreflightStatus FailureStatus { get; }
		public IReadOnlyList<SourceResourceAllocation> Allocations { get; }
		public IReadOnlyList<SourceSnapshotExpectation> Expectations { get; }
		public IReadOnlyList<ExecutionPreflightIssue> Issues { get; }

		public static AllocationAttempt Success(
			IReadOnlyList<SourceResourceAllocation> allocations,
			IReadOnlyList<SourceSnapshotExpectation> expectations) =>
			new(true, ExecutionPreflightStatus.Ready, allocations, expectations, Array.Empty<ExecutionPreflightIssue>());

		public static AllocationAttempt Failure(ExecutionPreflightStatus status, params ExecutionPreflightIssue[] issues) =>
			new(false, status, Array.Empty<SourceResourceAllocation>(), Array.Empty<SourceSnapshotExpectation>(), issues);
	}
}
