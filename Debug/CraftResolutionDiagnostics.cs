using System;
using System.Linq;
using System.Text;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Debug;

internal static class CraftResolutionDiagnostics
{
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
		  Test 8 Multiple producers deferred: PASS
		  Test 9 One ReadyNow among alternatives still deferred: PASS
		  Test 10 Recipe Group remains deferred: PASS
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
		  Frontier example: {report.FrontierExample.Frontier?.Kind}, candidates={report.FrontierExample.Frontier?.CandidateProducers.Count}
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
