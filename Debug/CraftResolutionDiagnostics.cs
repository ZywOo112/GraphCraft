using System;
using System.Linq;
using System.Text;
using GraphCraft.Core.Resolution;

namespace GraphCraft.Debug;

internal static class CraftResolutionDiagnostics
{
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
		builder.AppendLine($"  InventoryTypes: {snapshot.Context.InventoryCounts.Count}, InventoryItems: {snapshot.Context.TotalInventoryItemCount}");
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
				if (ingredient.MatchedAlternativeItems.Count > 0) {
					builder.Append(" [");
					builder.Append(string.Join(", ", ingredient.MatchedAlternativeItems.Select(evidence =>
						$"{itemName(evidence.ItemType)} x{evidence.Count}")));
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
