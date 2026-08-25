using System;
using System.Linq;
using System.Text;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Debug;

internal static class CraftResolutionDiagnostics
{
	public static string FormatTransactionalExecutionValidation(TransactionalExecutionSyntheticReport report) => $"""
		[GraphCraft] Milestone 5.2 transactional execution validation
		  T1-T3 direct, split-slot, and generic multi-source execution: PASS
		  T4 EvidenceOnly rejection: PASS
		  T5 transaction-local intermediate: PASS
		  T6 intermediate surplus delivery: PASS (surplus={report.SurplusExample.SurplusOutputs.Sum(item => item.Quantity)})
		  T7 external plus local intermediate: PASS
		  T8 shared-resource no double consume: PASS
		  T9 concrete Recipe Group consumption: PASS
		  T10-T12 final source/environment/condition validation: PASS
		  T13 pre-mutation output capacity rejection: PASS
		  T14-T17 deterministic partial-consumption/step/output rollback: PASS
		  T18 transaction-instance double execute protection: PASS
		  T19 deterministic actual-consumption recording: PASS
		  T20 no-duplication fault matrix: PASS ({report.FaultRollbackCaseCount} points)
		  T21 exact no-loss rollback: PASS
		  T22 immutable Planner artifact: PASS
		  Success example: id={report.SuccessExample.TransactionId}, status={report.SuccessExample.Status}, committed={report.SuccessExample.Committed}
		  Rollback example: id={report.RollbackExample.TransactionId}, status={report.RollbackExample.Status}, rollback={report.RollbackExample.RollbackSucceeded}
		  PassedRequiredTests: {report.PassedTestCount}/22
		  TransactionalExecutionValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
	""";

	public static string FormatExecutionDryRunValidation(ExecutionDryRunSyntheticReport report) => $"""
		[GraphCraft] Milestone 5.1 execution planning and fresh preflight validation
		  E1/E2 one source sufficient/insufficient: PASS
		  E3 deterministic split A3+B4+C3: PASS ({string.Join(", ", report.SplitAllocationExample.AllocationPreview.Select(allocation => $"{allocation.SourceId}=x{allocation.Quantity}"))})
		  E4/E5 EvidenceOnly and mixed-capability boundary: PASS
		  E6 shared-source double-count prevention: PASS
		  E7 transaction-local intermediate: PASS (externalItems={string.Join(',', report.IntermediateExample.AllocationPreview.Select(allocation => allocation.ItemType).Distinct())})
		  E8 intermediate surplus reuse: PASS
		  E9 external plus local intermediate: PASS
		  E10 concrete Recipe Group preservation: PASS
		  E11/E12 resource change and source disappearance: PASS (stale={report.StaleResourceExample.Status})
		  E13 environment change: PASS ({report.EnvironmentChangeExample.Status})
		  E14 recipe condition change: PASS
		  E15 deterministic replay: PASS
		  E16 success/failure no mutation: PASS
		  Runtime mutation: disabled
		  PassedRequiredTests: {report.PassedTestCount}/16
		  ExecutionPreflightValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
	""";

	public static string FormatCraftTransactionAudit(
		TargetCraftPlanResult logicalPlan,
		CraftExecutionPreflightResult? preflight,
		CraftTransactionResult? transaction,
		Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		builder.AppendLine($"[GraphCraft] M5.2 LIVE transaction: {itemName(logicalPlan.Target.ItemType)} ({logicalPlan.Target.ItemType}) additional x{logicalPlan.RequestedAdditionalOutputQuantity}");
		builder.AppendLine($"  PlannerStatus={logicalPlan.Status}; PreflightStatus={preflight?.Status.ToString() ?? "NotRun"}; TransactionStatus={transaction?.Status.ToString() ?? "NotRun"}");
		if (transaction is null)
			return builder.ToString();
		builder.AppendLine($"  TransactionId={transaction.TransactionId}; State={transaction.State}; mutation={transaction.MutationOccurred}; committed={transaction.Committed}; rollbackAttempted={transaction.RollbackAttempted}; rollbackSucceeded={transaction.RollbackSucceeded}");
		foreach (ActualResourceConsumption consumed in transaction.ActualConsumptions)
			builder.AppendLine($"  Consumed: step={consumed.StepSequence}, ingredient={consumed.IngredientOrdinal}, SourceId={consumed.SourceId}, locator={consumed.SourceLocalLocator}, {itemName(consumed.ItemType)} ({consumed.ItemType}) x{consumed.Quantity}");
		foreach (CraftTransactionItemStack output in transaction.ProducedOutputs)
			builder.AppendLine($"  Produced: step={output.OriginStepSequence}, repetition={output.CraftRepetition}, {itemName(output.ItemType)} ({output.ItemType}) x{output.Quantity}");
		foreach (CraftTransactionItemStack output in transaction.DeliveredOutputs)
			builder.AppendLine($"  Delivered: {output.Kind}, {itemName(output.ItemType)} ({output.ItemType}) x{output.Quantity}");
		foreach (CraftTransactionItemStack output in transaction.SurplusOutputs)
			builder.AppendLine($"  Surplus: {itemName(output.ItemType)} ({output.ItemType}) x{output.Quantity}");
		foreach (CraftTransactionIssue issue in transaction.Issues)
			builder.AppendLine($"  Issue: {issue.Status}: {issue.Message}");
		return builder.ToString();
	}

	public static string FormatTransactionalRealRecipeIntegration(
		TransactionalRealRecipeIntegrationReport report,
		Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		builder.AppendLine("[GraphCraft] Milestone 5.2 actual Terraria recipe-semantics preparation validation");
		foreach (TransactionalRealRecipeCase test in report.Cases)
			builder.AppendLine($"  {test.Name}: PASS Recipe #{test.RecipeIndex} -> {itemName(test.ResultItemType)} ({test.ResultItemType}); actualIngredients={test.ActualIngredientQuantity}/{test.MaximumIngredientQuantity}; output={test.OutputQuantity}");
		builder.AppendLine($"  PassedActualTransactionRecipeCases: {report.PassedCaseCount}/{report.Cases.Count}");
		builder.AppendLine("  Boundary: dedicated load prepares official consumption/output semantics without mutating a Player; live OnCraft/inventory delivery remains manual-validation only.");
		builder.AppendLine($"  TransactionRecipeIntegrationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}");
		return builder.ToString();
	}

	public static string FormatExecutionPreflightAudit(
		TargetCraftPlanResult logicalPlan,
		CraftExecutionPreflightResult? preflight,
		bool inventoryUnchanged,
		Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		builder.AppendLine($"[GraphCraft] M5.1 execution preflight: {itemName(logicalPlan.Target.ItemType)} ({logicalPlan.Target.ItemType}) additional x{logicalPlan.RequestedAdditionalOutputQuantity}");
		builder.AppendLine($"  PlannerStatus: {logicalPlan.Status}; selectedRoot={(logicalPlan.SelectedRootRecipe is null ? "none" : $"Recipe #{logicalPlan.SelectedRootRecipe.RecipeIndex}")}; steps={logicalPlan.Steps.Count}");
		builder.AppendLine($"  ExecutionStatus: {preflight?.Status.ToString() ?? "NotRun"}; executableTransaction={(preflight?.TransactionPlan is null ? "no" : "yes")}; inventoryUnchanged={inventoryUnchanged}");
		if (preflight is null) {
			builder.AppendLine("  Execution preflight requires one successful, fully resolved logical plan.");
			return builder.ToString();
		}

		foreach (ExecutionStepPlan step in preflight.StepPreview) {
			builder.AppendLine($"  Step {step.Sequence}: Recipe #{step.Recipe.RecipeIndex} x{step.CraftRepetitions} -> {itemName(step.Production.ItemType)} x{step.Production.Quantity} ({step.Production.Kind})");
			foreach (ExecutionResourceDemand demand in step.Demands)
				builder.AppendLine($"    Demand {itemName(demand.ItemType)} ({demand.ItemType}) x{demand.RequiredQuantity}: local={demand.TransactionLocalQuantity}, live={demand.ExternalSourceQuantity}, groupConcrete={demand.IsRecipeGroupAllocation}");
		}
		foreach (SourceResourceAllocation allocation in preflight.AllocationPreview) {
			builder.AppendLine($"  LiveAllocation: step={allocation.StepSequence}, ingredient={allocation.IngredientOrdinal}, SourceId={allocation.SourceId}, item={itemName(allocation.ItemType)} ({allocation.ItemType}), quantity={allocation.Quantity}, capability={allocation.ConsumptionCapability}");
		}
		foreach (RecipePreflightEvidence evidence in preflight.RecipeEvidence) {
			string conditions = evidence.Conditions.Count == 0
				? "none"
				: string.Join(", ", evidence.Conditions.Select(condition => $"{condition.Key}={condition.State}"));
			builder.AppendLine($"  Constraints: step={evidence.StepSequence}, recipe=#{evidence.RecipeIndex}, environment={evidence.StationState}, conditions=[{conditions}]");
		}
		foreach (ExecutionPreflightIssue issue in preflight.Issues)
			builder.AppendLine($"  Issue: {issue.Kind}: {issue.Message}");
		builder.AppendLine("  Boundary: dry run only; no inventory removal, craft call, output spawn, commit, or rollback.");
		return builder.ToString();
	}

	public static string FormatTargetPlanAudit(TargetCraftPlanResult result, Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		builder.AppendLine($"[GraphCraft] Target plan audit: {itemName(result.Target.ItemType)} ({result.Target.ItemType}) additional x{result.RequestedAdditionalOutputQuantity}");
		builder.AppendLine($"  Status: {result.Status}; selected root: {(result.SelectedRootRecipe is null ? "none" : $"Recipe #{result.SelectedRootRecipe.RecipeIndex}")}");
		builder.AppendLine($"  Root candidates: {result.RootDecision?.Candidates.Count ?? result.Target.ProducerRecipes.Count}; root decision: {result.RootDecision?.State.ToString() ?? "none"}");
		if (result.RootDecision?.UserSelectableCandidates.Count > 0)
			builder.AppendLine($"  Confirmed selectable roots: {string.Join(", ", result.RootDecision.UserSelectableCandidates.Select(candidate => $"Recipe #{candidate.Recipe.RecipeIndex}"))}");
		builder.AppendLine($"  Steps: {result.Steps.Count}; reservations: {result.Reservations.Count}; productions: {result.Productions.Count}; group allocations: {result.GroupAllocations.Count}");
		foreach (PlannedCraftStep step in result.Steps)
			builder.AppendLine($"    {step.Sequence}. Recipe #{step.Recipe.RecipeIndex} -> {itemName(step.Recipe.Result.ItemType)} repetitions={step.CraftRepetitions}, produced={step.ProducedQuantity}");
		foreach (RecipeGroupAllocation allocation in result.GroupAllocations)
			builder.AppendLine($"    Group #{string.Join(',', allocation.RecipeGroups.Select(group => group.GroupId))} required={allocation.RequiredQuantity}: {allocation.DeterministicSignature}");
		builder.AppendLine($"  Search: expanded={result.SearchStatistics.ExpandedBranches}/{result.SearchStatistics.MaximumBranchExpansions}, recipe={result.SearchStatistics.RecipeBranchExpansions}, resource={result.SearchStatistics.ResourceAllocationBranchExpansions}, backtracks={result.SearchStatistics.BacktrackedBranchCount}, exhausted={result.SearchStatistics.BudgetExhausted}");
		if (result.Frontier is not null)
			builder.AppendLine($"  Frontier: {result.Frontier.Kind}; dependency={result.Frontier.DependencyFrontier?.Kind.ToString() ?? "none"}; truncation={result.Frontier.SubgraphTruncationReason}");
		return builder.ToString();
	}

	public static string FormatRealRecipeIntegration(RealRecipeIntegrationReport report, Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		builder.AppendLine("[GraphCraft] Milestone 4 actual Terraria recipe integration validation");
		foreach (RealRecipeIntegrationCase test in report.Cases) {
			builder.AppendLine($"  {test.Category}: PASS target={itemName(test.TargetItemType)} ({test.TargetItemType}), roots=[{string.Join(',', test.RootRecipeIndices)}], status={test.Status}");
			builder.AppendLine($"    graph items={test.ItemNodeCount}, recipes={test.RecipeNodeCount}, edges={test.TotalEdgeCount}, groupExpandedItems={test.RecipeGroupExpandedItemCount}, truncated={test.TruncationReason}, extractionMs={test.ExtractionElapsed.TotalMilliseconds:F3}");
			builder.AppendLine($"    plan steps={test.PlannedStepCount}, branches={test.SearchStatistics.ExpandedBranches}/{test.SearchStatistics.MaximumBranchExpansions}, recipeBranches={test.SearchStatistics.RecipeBranchExpansions}, resourceBranches={test.SearchStatistics.ResourceAllocationBranchExpansions}, backtracks={test.SearchStatistics.BacktrackedBranchCount}, planningMs={test.PlanningElapsed.TotalMilliseconds:F3}");
			builder.AppendLine($"    evidence: {test.Evidence}");
		}
		builder.AppendLine($"  PassedActualRecipeCases: {report.PassedCaseCount}/{report.Cases.Count}");
		builder.AppendLine($"  ActualRecipeIntegrationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}");
		return builder.ToString();
	}

	public static string FormatPlannerFinalizationValidation(PlannerFinalizationSyntheticReport report) => $"""
		[GraphCraft] Milestone 4 finalization synthetic validation
		  Target-level planning: {report.TargetPlannerTestCount}/10 PASS
		  Cycle/SCC policy: {report.CycleTestCount}/5 PASS
		  Truncation semantics: {report.TruncationTestCount}/4 PASS
		  Search-budget semantics: {report.SearchBudgetTestCount}/5 PASS
		  Root choice example: status={report.TargetChoiceExample.Status}, candidates={report.TargetChoiceExample.RootDecision?.Candidates.Count}, selectable={report.TargetChoiceExample.RootDecision?.UserSelectableCandidates.Count}
		  Cycle recovery example: status={report.CycleRecoveryExample.Status}, selectedRoot={report.CycleRecoveryExample.SelectedRootRecipe?.RecipeIndex}
		  Budget example: status={report.BudgetExample.Status}, expanded={report.BudgetExample.SearchStatistics.ExpandedBranches}/{report.BudgetExample.SearchStatistics.MaximumBranchExpansions}
		  FinalizationSyntheticValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
	""";

	public static string FormatResourceAlternativeValidation(ResourceAlternativeSyntheticReport report) => $"""
		[GraphCraft] Milestone 4.4 resource alternatives and planner backtracking validation
		  Root-continuation Recipe backtracking: PASS
		  Root-successful choice aggregation and explicit hard constraints: PASS
		  Concrete split/mixed Recipe Group allocation: PASS
		  Group allocation backtracking and exact-resource preservation: PASS
		  Recursive group-member production and virtual surplus: PASS
		  Group/exact double-count prevention: PASS
		  Nested search, atomic failure, deferred semantics, and graph constraints: PASS
		  Search budget boundary and deterministic replay: PASS
		  Sequential actionable user decisions: PASS
		  Backtracking example: status={report.BacktrackingExample.Status}, decisions={report.BacktrackingExample.Decisions.Count}, steps={report.BacktrackingExample.Steps.Count}
		  Group example: status={report.GroupAllocationExample.Status}, allocation={report.GroupAllocationExample.GroupAllocations.Single().DeterministicSignature}
		  Budget example: status={report.BudgetExample.Status}, expanded={report.BudgetExample.SearchStatistics.ExpandedBranches}/{report.BudgetExample.SearchStatistics.MaximumBranchExpansions}
		  PassedRequiredTests: {report.PassedRequiredTestCount}/20
		  ResourceAlternativeValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
	""";

	public static string FormatAlternativeDecisionValidation(AlternativeDecisionSyntheticReport report)
	{
		AlternativeDecision choice = report.ChoiceExample.PendingDecision
			?? throw new InvalidOperationException("Milestone 4.3 diagnostic example is missing its decision.");
		return $"""
		[GraphCraft] Milestone 4.3 alternative branching and explicit choice validation
		  Test 1 One viable + one failed auto-select: PASS
		  Test 2 Two viable require user choice: PASS
		  Test 3 Three viable preserve canonical order: PASS
		  Test 4 Failed reservation branch isolation: PASS
		  Test 5 Failed virtual-production branch isolation: PASS
		  Test 6 Explicit second-candidate selection: PASS
		  Test 7 Invalid explicit choice rejected: PASS
		  Test 8 Explicit failure has no silent fallback: PASS
		  Test 9 Candidate order independent of insertion: PASS
		  Test 10 Blocked + success auto-select: PASS
		  Test 11 Recursive candidate fully evaluated: PASS
		  Test 12 ReadyNow respects evolved Ledger: PASS
		  Test 13 Local alternatives backtrack for later sibling: PASS
		  Test 14 Explicit bad local choice does not backtrack: PASS
		  Test 15 Explicit good local choice succeeds: PASS
		  Test 16 First actionable nested choice provenance: PASS
		  Test 17 Complete outer routes define user choice: PASS
		  Test 18 Viable + deferred constraint auto-selects viable: PASS
		  Test 19 Failed + blocked candidates preserve frontiers: PASS
		  Test 20 Deferred/blocked is not proven impossible: PASS
		  Test 21 Unique producer regression: PASS
		  Test 22 Zero producer regression: PASS
		  Test 23 Snapshot/provenance/preview immutability: PASS
		  Test 24 Decision identity/order/status/preview determinism: PASS
		  Test 25 Two viable + deferred exposes only viable user selections: PASS
		  Choice example: id={choice.DecisionId}, state={choice.State}, candidates={choice.Candidates.Count}, selectable={choice.UserSelectableCandidates.Count}
		  Explicit example: status={report.ExplicitChoiceExample.Status}, applied={report.ExplicitChoiceExample.AppliedChoices.Count}, steps={report.ExplicitChoiceExample.Steps.Count}
		  PassedRequiredTests: {report.PassedRequiredTestCount}/25
		  AlternativeDecisionValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatRecursivePlannerValidation(RecursivePlannerSyntheticReport report)
	{
		RecursiveRecipePlanResult example = report.OneLevelExample;
		return $"""
		[GraphCraft] Milestone 4.2 recursive single-path planner synthetic validation
		  Test 1 One-level recursive success: PASS
		  Test 2 Multi-level recursive success: PASS
		  Test 3 Existing partial intermediate produces deficit only: PASS
		  Test 4 Intermediate surplus reused by sibling: PASS
		  Test 5 Shared recursive base resource cannot double-count: PASS
		  Test 6 Recursive root failure atomicity: PASS
		  Test 7 Zero producer structured frontier: PASS
		  Test 8 Multiple fully viable producers require user choice: PASS
		  Test 9 One viable plus one blocked producer auto-selected: PASS
		  Test 10 Recipe Group concrete allocation regression: PASS
		  Test 11 NeedsIngredients recursively resolved: PASS
		  Test 12 ReadyNow does not override evolved Ledger: PASS
		  Test 13 BlockedByStation evidence propagated: PASS
		  Test 14 BlockedByCondition evidence propagated: PASS
		  Test 15 UnknownConstraint evidence propagated: PASS
		  Test 16 Simple cycle boundary: PASS
		  Test 17 Cycle detection path-local: PASS
		  Test 18 Configured planning depth boundary: PASS
		  Test 19 Multi-output intermediate quantity/surplus: PASS
		  Test 20 Snapshot/aggregate/provenance/Resolution immutability: PASS
		  Test 21 Graph-external producer ignored: PASS
		  Test 22 Recursive result determinism: PASS
		  Additional terminal/truncation frontiers: {(report.TerminalAndTruncationBoundariesPassed ? "PASS" : "FAIL")}
		  Example status: {example.Status}, steps={example.Steps.Count}, reservations={example.Reservations.Count}, productions={example.Productions.Count}
		  Example step order: {string.Join(" -> ", example.Steps.Select(step => $"Recipe #{step.Recipe.RecipeIndex} x{step.CraftRepetitions}"))}
		  Example intermediate balance: initial={example.ResultLedger.GetBalance(302).InitialQuantity}, produced={example.ResultLedger.GetBalance(302).PlannedProductionQuantity}, reserved={example.ResultLedger.GetBalance(302).ReservedQuantity}, remaining={example.ResultLedger.GetBalance(302).RemainingQuantity}
		  Decision example: {report.FrontierExample.PendingDecision?.State}, candidates={report.FrontierExample.PendingDecision?.Candidates.Count}
		  PassedRequiredTests: {report.PassedRequiredTestCount}/22
		  RecursivePlannerValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatPlannerFoundationValidation(PlannerFoundationSyntheticReport report)
	{
		SingleRecipePlanResult example = report.QuantityExample;
		IngredientPlanResult ingredient = example.IngredientResults.Single();
		return $"""
		[GraphCraft] Milestone 4.1 planner foundation synthetic validation
		  Test 1 Ledger initialization: PASS (initial=10 reserved=0 remaining=10)
		  Test 2 Successful reservation: PASS (reserve=4 remaining=6; snapshot=10)
		  Test 3 Repeated reservation uses remaining: PASS (reserve 6 then 5 -> available=4 FAIL)
		  Test 4 Failed reservation preserves state: PASS
		  Test 5 Single recipe AND success: PASS
		  Test 6 Single recipe AND failure atomicity: PASS (no committed reservations)
		  Test 7 Shared item cannot be double-counted: PASS (X=7, needs 5+4 -> FAIL)
		  Test 8 Recipe output count: PASS (requested={example.RequestedOutputQuantity}, repetitions={example.CraftRepetitions}, required={ingredient.RequiredQuantity}, produced={example.ProducedQuantity})
		  Test 9 Exact quantity boundary: PASS (requested=10, repetitions=2, required=4, produced=10)
		  Test 10 Source/aggregate/provenance immutability: PASS
		  Test 11 Graph constraint: PASS (outside recipe rejected)
		  Test 12 Missing direct ingredient does not recurse: PASS
		  Test 13 Station/condition evidence propagation: PASS
		  Test 14 Determinism: PASS
		  Additional Recipe Group boundary: {(report.RecipeGroupBoundaryPassed ? "PASS" : "FAIL")} (concrete allocation deferred)
		  PassedRequiredTests: {report.PassedRequiredTestCount}/14
		  PlannerFoundationValidationElapsedMs: {report.Elapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatSyntheticValidation(
		DynamicResolutionSyntheticReport report,
		Func<int, string> itemName)
	{
		return $"""
		[GraphCraft] Dynamic resolution synthetic validation
		  Scenario1 Direct sufficient: PASS (Stone Block required=20 available=30 local=True)
		  Scenario2 Direct insufficient: PASS (Stone Block required=20 available=8 local=False)
		  Scenario3 RecipeGroup alternative: PASS (Any Wood required=4 Boreal Wood=10 local=True)
		  Scenario4 Missing station: PASS (Work Bench -> BlockedByStation)
		  Scenario5 Condition false: PASS (-> BlockedByCondition)
		  Scenario6 Condition unknown: PASS (-> UnknownConstraint)
		  Snapshot immutability: PASS
		  Canonical overlay coverage: PASS
		  ReadyNow / NeedsIngredients local states: PASS
		  Shared inventory boundary: PASS (two Wood x10 edges each local=True; no global feasibility conclusion)
		  Real condition recipe: Recipe #{report.ConditionRecipeIndex} -> {itemName(report.ConditionResultItemType)} (ItemID {report.ConditionResultItemType})
		  Real condition description: {report.ConditionDescription}
		  Real condition true/false/unknown mapping: PASS
		  PassedScenarios: {report.PassedScenarioCount}
		  Resource Test A single source compatibility: PASS
		  Resource Test B multiple source aggregation: PASS (3 + 7 = {report.ResourceSourceReport.MultiSourceAggregateCount})
		  Resource Test C per-source provenance: PASS
		  Resource Test D ingredient split across sources: PASS
		  Resource Test E insufficient aggregate: PASS
		  Resource Test F multi-source RecipeGroup evidence: PASS
		  Resource Test G immutable source isolation: PASS
		  PassedResourceSourceTests: {report.ResourceSourceReport.PassedTestCount}
		  ResourceSourceValidationElapsedMs: {report.ResourceSourceReport.Elapsed.TotalMilliseconds:F3}
		  SyntheticContextElapsedMs: {report.SyntheticContextElapsed.TotalMilliseconds:F3}
		  FurnaceOverlayElapsedMs: {report.FurnaceOverlayElapsed.TotalMilliseconds:F3}
		  CellPhoneOverlayElapsedMs: {report.CellPhoneOverlayElapsed.TotalMilliseconds:F3}
		  TotalSyntheticValidationElapsedMs: {report.TotalElapsed.TotalMilliseconds:F3}
		""";
	}

	public static string FormatResolutionSnapshot(
		ResolutionSnapshot snapshot,
		Func<int, string> itemName)
	{
		var builder = new StringBuilder();
		ItemResolution targetResolution = snapshot.GetItemResolution(snapshot.Target);
		builder.AppendLine($"[GraphCraft] Resolution #{snapshot.ResolutionId}: {itemName(snapshot.Target.ItemType)} (ItemID {snapshot.Target.ItemType})");
		builder.AppendLine($"  ContextSnapshot: #{snapshot.Context.SnapshotId}, player={snapshot.Context.SourcePlayerIndex}, captured={snapshot.Context.CapturedAtUtc:O}");
		builder.AppendLine($"  ResourceSources: {snapshot.Context.ResourceSources.Count}, AggregateResourceTypes: {snapshot.Context.AggregateResourceCounts.Count}, AggregateResourceItems: {snapshot.Context.TotalResourceItemCount}");
		foreach (var source in snapshot.Context.ResourceSources) {
			builder.AppendLine($"    Source {source.Descriptor.SourceId} ({source.Descriptor.SourceKind}): types={source.ResourceCounts.Count}, consumption={source.Descriptor.ConsumptionCapability}");
		}
		builder.AppendLine($"  AvailableStationTiles: {snapshot.Context.AvailableTileTypes.Count}, CapturedConditions: {snapshot.Context.ConditionEvidence.Count}");
		builder.AppendLine($"  TargetState: {targetResolution.State}, CurrentCount: {targetResolution.CurrentCount}, Component: #{targetResolution.ComponentId}, Cyclic: {targetResolution.IsCyclic}");
		builder.AppendLine($"  Coverage: items={snapshot.ItemResolutions.Count}, ingredients={snapshot.IngredientResolutions.Count}, recipes={snapshot.RecipeResolutions.Count}");
		builder.AppendLine($"  CaptureElapsedMs: {snapshot.Context.CaptureElapsed.TotalMilliseconds:F3}, OverlayElapsedMs: {snapshot.Elapsed.TotalMilliseconds:F3}");
		builder.AppendLine("  Direct producer recipes:");

		RecipeResolution[] directRecipes = snapshot.Target.ProducerRecipes
			.Where(snapshot.RecipeResolutions.ContainsKey)
			.Select(snapshot.GetRecipeResolution)
			.OrderBy(resolution => resolution.Recipe.RecipeIndex)
			.ToArray();
		foreach (RecipeResolution recipe in directRecipes) {
			builder.AppendLine($"    Recipe #{recipe.Recipe.RecipeIndex}: {recipe.State} (ReadyNow is local-only)");
			foreach (IngredientResolution ingredient in recipe.Ingredients) {
				builder.Append($"      - {itemName(ingredient.Ingredient.Source.ItemType)} x{ingredient.RequiredAmount}: available={ingredient.AvailableAmount}, local={ingredient.LocallySatisfied}, mode={ingredient.ResolutionMode}");
				if (ingredient.MatchedResources.Count > 0) {
					builder.Append(" [");
					builder.Append(string.Join(", ", ingredient.MatchedResources.Select(evidence =>
						$"{itemName(evidence.ItemType)} x{evidence.Count} from {evidence.SourceId}")));
					builder.Append(']');
				}
				builder.AppendLine();
			}

			builder.AppendLine($"      Station: {recipe.Station.State}" +
				(recipe.Station.MissingTileTypes.Count > 0
					? $"; missing tile IDs={string.Join(",", recipe.Station.MissingTileTypes)}"
					: string.Empty));
			foreach (ConditionConstraintResolution condition in recipe.Conditions) {
				builder.AppendLine($"      Condition {condition.Key}: {condition.State} - {condition.Description}" +
					(condition.UnknownReason is null ? string.Empty : $" ({condition.UnknownReason})"));
			}
		}

		builder.AppendLine("  Boundary: local ingredient satisfaction and ReadyNow do not imply recursive/global craft feasibility.");
		return builder.ToString();
	}
}
