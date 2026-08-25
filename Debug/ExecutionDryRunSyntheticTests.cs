using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Execution;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using Builder = GraphCraft.Debug.RecursivePlannerSyntheticTests.SyntheticGraphBuilder;
using Fixture = GraphCraft.Debug.RecursivePlannerSyntheticTests.Fixture;
using Spec = GraphCraft.Debug.RecursivePlannerSyntheticTests.IngredientSpec;

namespace GraphCraft.Debug;

internal sealed record ExecutionDryRunSyntheticReport(
	int PassedTestCount,
	CraftExecutionPreflightResult SplitAllocationExample,
	CraftExecutionPreflightResult IntermediateExample,
	CraftExecutionPreflightResult StaleResourceExample,
	CraftExecutionPreflightResult EnvironmentChangeExample,
	TimeSpan Elapsed);

internal static class ExecutionDryRunSyntheticTests
{
	public static ExecutionDryRunSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		TestE1OneSourceEnough();
		TestE2OneSourceInsufficient();
		CraftExecutionPreflightResult split = TestE3DeterministicSplit();
		TestE4EvidenceOnlyRejected();
		TestE5MixedCapabilityInsufficient();
		TestE6SharedSourceCannotDoubleCount();
		CraftExecutionPreflightResult intermediate = TestE7LocalIntermediate();
		TestE8IntermediateSurplusReuse();
		TestE9ExternalPlusIntermediate();
		TestE10RecipeGroupAlreadyConcrete();
		CraftExecutionPreflightResult stale = TestE11ResourceStateChanged();
		TestE12SourceDisappeared();
		CraftExecutionPreflightResult environment = TestE13EnvironmentChanged();
		TestE14ConditionChanged();
		TestE15DeterministicReplay();
		TestE16NoMutationOnSuccessOrFailure();
		timer.Stop();
		return new ExecutionDryRunSyntheticReport(16, split, intermediate, stale, environment, timer.Elapsed);
	}

	private static void TestE1OneSourceEnough()
	{
		CraftResourceSourceSnapshot source = Source("m51:e1", (5101, 5));
		Fixture fixture = DirectFixture(1, 5100, 5101, 5, source);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, source);
		Assert(result.Succeeded && result.TransactionPlan is not null &&
			result.AllocationPreview.Single().SourceId == source.Descriptor.SourceId &&
			result.AllocationPreview.Single().Quantity == 5,
			"E1 one sufficient source was not allocated exactly.");
	}

	private static void TestE2OneSourceInsufficient()
	{
		CraftResourceSourceSnapshot source = Source("m51:e2", (5111, 4));
		Fixture fixture = DirectFixture(2, 5110, 5111, 5, source);
		TargetCraftPlanResult logical = Plan(fixture);
		CraftExecutionPreflightResult result = Execute(logical, fixture, source);
		Assert(!logical.Succeeded && result.Status == ExecutionPreflightStatus.LogicalPlanNotExecutable &&
			result.TransactionPlan is null,
			"E2 accepted an insufficient logical plan.");
	}

	private static CraftExecutionPreflightResult TestE3DeterministicSplit()
	{
		CraftResourceSourceSnapshot c = Source("m51:e3:c", (5121, 8));
		CraftResourceSourceSnapshot a = Source("m51:e3:a", (5121, 3));
		CraftResourceSourceSnapshot b = Source("m51:e3:b", (5121, 4));
		Fixture fixture = DirectFixture(3, 5120, 5121, 10, c, a, b);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, c, a, b);
		Assert(result.Succeeded &&
			result.AllocationPreview.Select(allocation => allocation.SourceId.Value)
				.SequenceEqual(new[] { "m51:e3:a", "m51:e3:b", "m51:e3:c" }) &&
			result.AllocationPreview.Select(allocation => allocation.Quantity).SequenceEqual(new long[] { 3, 4, 3 }),
			"E3 A=3/B=4/C=8, need=10 did not split as A3+B4+C3.");
		return result;
	}

	private static void TestE4EvidenceOnlyRejected()
	{
		CraftResourceSourceSnapshot source = Source("m51:e4", CraftResourceConsumptionCapability.EvidenceOnly, (5131, 100));
		Fixture fixture = DirectFixture(4, 5130, 5131, 5, source);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, source);
		Assert(result.Status == ExecutionPreflightStatus.UnsupportedSourceCapability &&
			result.Issues.Single().Kind == ExecutionPreflightIssueKind.EvidenceOnlyResourceRequired &&
			result.TransactionPlan is null,
			"E4 allowed EvidenceOnly resources into a transaction allocation.");
	}

	private static void TestE5MixedCapabilityInsufficient()
	{
		CraftResourceSourceSnapshot consumable = Source("m51:e5:a", (5141, 3));
		CraftResourceSourceSnapshot evidence = Source("m51:e5:b", CraftResourceConsumptionCapability.EvidenceOnly, (5141, 100));
		Fixture fixture = DirectFixture(5, 5140, 5141, 5, consumable, evidence);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, consumable, evidence);
		Assert(result.Status == ExecutionPreflightStatus.UnsupportedSourceCapability &&
			result.AllocationPreview.Count == 0 && result.TransactionPlan is null,
			"E5 published a partial plan for consumable=3 plus EvidenceOnly=100, need=5.");
	}

	private static void TestE6SharedSourceCannotDoubleCount()
	{
		CraftResourceSourceSnapshot consumable = Source("m51:e6:a", (5151, 10));
		CraftResourceSourceSnapshot evidence = Source("m51:e6:b", CraftResourceConsumptionCapability.EvidenceOnly, (5151, 2));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(6, 5150, 1, RecipeLocalState.ReadyNow, true,
			new Spec(5151, 6, false), new Spec(5151, 6, false));
		Fixture fixture = builder.Build(root, Context(consumable, evidence));
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, consumable, evidence);
		Assert(result.Status == ExecutionPreflightStatus.UnsupportedSourceCapability && result.TransactionPlan is null,
			"E6 double-counted a shared consumable source across X6 + X6 with only X10 allocatable.");
	}

	private static CraftExecutionPreflightResult TestE7LocalIntermediate()
	{
		CraftResourceSourceSnapshot source = Source("m51:e7", (5162, 2));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(7, 5160, 1, RecipeLocalState.NeedsIngredients, true, new Spec(5161, 1, false));
		builder.AddRecipeWithIndex(70, 5161, 1, RecipeLocalState.NeedsIngredients, true, new Spec(5162, 2, false));
		Fixture fixture = builder.Build(root, Context(source));
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, source);
		ExecutionResourceDemand rootDemand = result.StepPreview.Single(step => step.Recipe == root).Demands.Single();
		Assert(result.Succeeded && rootDemand.TransactionLocalQuantity == 1 && rootDemand.ExternalSourceQuantity == 0 &&
			result.AllocationPreview.All(allocation => allocation.ItemType == 5162),
			"E7 allocated a transaction-local intermediate from a live SourceId.");
		return result;
	}

	private static void TestE8IntermediateSurplusReuse()
	{
		CraftResourceSourceSnapshot source = Source("m51:e8", (5172, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(8, 5170, 1, RecipeLocalState.NeedsIngredients, true,
			new Spec(5171, 2, false), new Spec(5171, 3, false));
		builder.AddRecipeWithIndex(80, 5171, 5, RecipeLocalState.NeedsIngredients, true, new Spec(5172, 1, false));
		Fixture fixture = builder.Build(root, Context(source));
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, source);
		ExecutionResourceDemand[] rootDemands = result.StepPreview.Single(step => step.Recipe == root).Demands.ToArray();
		Assert(result.Succeeded && rootDemands.Sum(demand => demand.TransactionLocalQuantity) == 5 &&
			rootDemands.Sum(demand => demand.ExternalSourceQuantity) == 0 && result.AllocationPreview.Sum(a => a.Quantity) == 1,
			"E8 did not reuse transaction-local surplus across sibling requirements.");
	}

	private static void TestE9ExternalPlusIntermediate()
	{
		CraftResourceSourceSnapshot source = Source("m51:e9", (5181, 2), (5182, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(9, 5180, 1, RecipeLocalState.NeedsIngredients, true, new Spec(5181, 5, false));
		builder.AddRecipeWithIndex(90, 5181, 3, RecipeLocalState.NeedsIngredients, true, new Spec(5182, 1, false));
		Fixture fixture = builder.Build(root, Context(source));
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, source);
		ExecutionResourceDemand demand = result.StepPreview.Single(step => step.Recipe == root).Demands.Single();
		Assert(result.Succeeded && demand.TransactionLocalQuantity == 3 && demand.ExternalSourceQuantity == 2 &&
			result.AllocationPreview.Single(allocation => allocation.ItemType == 5181).Quantity == 2,
			"E9 did not combine an external balance with a produced intermediate.");
	}

	private static void TestE10RecipeGroupAlreadyConcrete()
	{
		CraftResourceSourceSnapshot wood = Source("m51:e10:a", (5191, 1));
		CraftResourceSourceSnapshot alternate = Source("m51:e10:b", (15191, 2));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(10, 5190, 1, RecipeLocalState.ReadyNow, true, new Spec(5191, 3, true));
		Fixture fixture = builder.Build(root, Context(wood, alternate));
		TargetCraftPlanResult logical = Plan(fixture);
		CraftExecutionPreflightResult result = Execute(logical, fixture, wood, alternate);
		Assert(result.Succeeded && logical.GroupAllocations.Single().DeterministicSignature == "5191:1,15191:2" &&
			result.StepPreview.Single().Demands.Select(d => $"{d.ItemType}:{d.RequiredQuantity}")
				.SequenceEqual(new[] { "5191:1", "15191:2" }),
			"E10 re-resolved or lost the Planner's concrete Recipe Group allocation.");
	}

	private static CraftExecutionPreflightResult TestE11ResourceStateChanged()
	{
		CraftResourceSourceSnapshot original = Source("m51:e11", (5201, 10));
		CraftResourceSourceSnapshot fresh = Source("m51:e11", (5201, 9));
		Fixture fixture = DirectFixture(11, 5200, 5201, 10, original);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture, fresh);
		Assert(result.Status == ExecutionPreflightStatus.SourceQuantityChanged &&
			result.Issues.Single().Kind == ExecutionPreflightIssueKind.SourceQuantityDecreased &&
			result.TransactionPlan is null,
			"E11 did not reject an allocated quantity changed after logical planning.");
		return result;
	}

	private static void TestE12SourceDisappeared()
	{
		CraftResourceSourceSnapshot original = Source("m51:e12", (5211, 2));
		Fixture fixture = DirectFixture(12, 5210, 5211, 2, original);
		CraftExecutionPreflightResult result = Execute(Plan(fixture), fixture);
		Assert(result.Status == ExecutionPreflightStatus.SourceUnavailable &&
			result.Issues.Single().Kind == ExecutionPreflightIssueKind.SourceUnavailable,
			"E12 did not reject a SourceId absent from the fresh snapshot.");
	}

	private static CraftExecutionPreflightResult TestE13EnvironmentChanged()
	{
		CraftResourceSourceSnapshot source = Source("m51:e13", (5221, 1));
		Fixture fixture = DirectFixture(13, 5220, 5221, 1, source);
		TargetCraftPlanResult logical = Plan(fixture);
		ResolutionSnapshot fresh = FreshResolution(fixture, Context(source), RecipeLocalState.BlockedByStation);
		CraftExecutionPreflightResult result = CraftExecutionPlanner.CreatePreflight(new(logical, fresh));
		Assert(result.Status == ExecutionPreflightStatus.EnvironmentChanged &&
			result.Issues.Single().Kind == ExecutionPreflightIssueKind.StationUnsatisfied &&
			result.TransactionPlan is null,
			"E13 did not reject a changed station environment.");
		return result;
	}

	private static void TestE14ConditionChanged()
	{
		CraftResourceSourceSnapshot source = Source("m51:e14", (5231, 1));
		Fixture fixture = DirectFixture(14, 5230, 5231, 1, source);
		TargetCraftPlanResult logical = Plan(fixture);
		ResolutionSnapshot fresh = FreshResolution(fixture, Context(source), RecipeLocalState.BlockedByCondition);
		CraftExecutionPreflightResult result = CraftExecutionPlanner.CreatePreflight(new(logical, fresh));
		Assert(result.Status == ExecutionPreflightStatus.ConditionChanged &&
			result.Issues.Single().Kind == ExecutionPreflightIssueKind.ConditionUnsatisfied &&
			result.TransactionPlan is null,
			"E14 did not reject a changed recipe condition.");
	}

	private static void TestE15DeterministicReplay()
	{
		CraftResourceSourceSnapshot z = Source("m51:e15:z", (5241, 4));
		CraftResourceSourceSnapshot a = Source("m51:e15:a", (5241, 4));
		Fixture fixture = DirectFixture(15, 5240, 5241, 6, z, a);
		TargetCraftPlanResult logical = Plan(fixture);
		CraftExecutionPreflightResult first = Execute(logical, fixture, z, a);
		CraftExecutionPreflightResult second = Execute(logical, fixture, a, z);
		Assert(Signature(first) == Signature(second) &&
			first.AllocationPreview.Select(allocation => allocation.SourceId.Value).SequenceEqual(new[] { "m51:e15:a", "m51:e15:z" }),
			"E15 allocation depended on fresh source insertion order.");
	}

	private static void TestE16NoMutationOnSuccessOrFailure()
	{
		CraftResourceSourceSnapshot original = Source("m51:e16", (5251, 4));
		Fixture fixture = DirectFixture(16, 5250, 5251, 4, original);
		TargetCraftPlanResult logical = Plan(fixture);
		string before = SnapshotSignature(original);
		CraftExecutionPreflightResult success = Execute(logical, fixture, original);
		CraftResourceSourceSnapshot stale = Source("m51:e16", (5251, 3));
		string staleBefore = SnapshotSignature(stale);
		CraftExecutionPreflightResult failure = Execute(logical, fixture, stale);
		Assert(success.Succeeded && !success.MutatesRuntimeState && success.TransactionPlan is { MutatesRuntimeState: false } &&
			failure.Status == ExecutionPreflightStatus.SourceQuantityChanged && !failure.MutatesRuntimeState &&
			before == SnapshotSignature(original) && staleBefore == SnapshotSignature(stale),
			"E16 success or failure mutated a resource snapshot.");
	}

	private static Fixture DirectFixture(
		int recipeIndex,
		int targetItemType,
		int ingredientItemType,
		int requiredQuantity,
		params CraftResourceSourceSnapshot[] sources)
	{
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(recipeIndex, targetItemType, 1, RecipeLocalState.ReadyNow, true,
			new Spec(ingredientItemType, requiredQuantity, false));
		return builder.Build(root, Context(sources));
	}

	private static TargetCraftPlanResult Plan(Fixture fixture) =>
		TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Subgraph.Target,
			1,
			fixture.Ledger));

	private static CraftExecutionPreflightResult Execute(
		TargetCraftPlanResult logical,
		Fixture fixture,
		params CraftResourceSourceSnapshot[] freshSources)
	{
		ResolutionSnapshot fresh = FreshResolution(fixture, Context(freshSources));
		return CraftExecutionPlanner.CreatePreflight(new CraftExecutionPreflightRequest(logical, fresh));
	}

	private static ResolutionSnapshot FreshResolution(
		Fixture fixture,
		PlayerCraftContextSnapshot freshContext,
		RecipeLocalState? rootState = null)
	{
		var recipes = fixture.Resolution.RecipeResolutions.ToDictionary(pair => pair.Key, pair => pair.Value);
		if (rootState is RecipeLocalState state) {
			RecipeResolution original = recipes[fixture.Root];
			StationConstraintResolution station = state == RecipeLocalState.BlockedByStation
				? new StationConstraintResolution(StationConstraintState.Unsatisfied, new[] { 18 }, new[] { 18 })
				: new StationConstraintResolution(StationConstraintState.Satisfied, Array.Empty<int>(), Array.Empty<int>());
			IReadOnlyList<ConditionConstraintResolution> conditions = state == RecipeLocalState.BlockedByCondition
				? new[] { new ConditionConstraintResolution(new RecipeConditionKey(fixture.Root.RecipeIndex, 0), "Synthetic changed condition", ConditionConstraintState.Unsatisfied, null) }
				: Array.Empty<ConditionConstraintResolution>();
			recipes[fixture.Root] = original with { State = state, Station = station, Conditions = conditions };
		}
		return new ResolutionSnapshot(
			fixture.Subgraph,
			freshContext,
			fixture.Resolution.ItemResolutions.ToDictionary(pair => pair.Key, pair => pair.Value),
			fixture.Resolution.IngredientResolutions.ToDictionary(pair => pair.Key, pair => pair.Value),
			recipes,
			TimeSpan.Zero);
	}

	private static PlayerCraftContextSnapshot Context(params CraftResourceSourceSnapshot[] sources) =>
		PlayerCraftContextSnapshot.CreateSynthetic(sources);

	private static CraftResourceSourceSnapshot Source(
		string id,
		params (int ItemType, int Count)[] resources) =>
		Source(id, CraftResourceConsumptionCapability.AdapterTransactionRequired, resources);

	private static CraftResourceSourceSnapshot Source(
		string id,
		CraftResourceConsumptionCapability capability,
		params (int ItemType, int Count)[] resources)
	{
		var descriptor = new CraftResourceSourceDescriptor(
			new CraftResourceSourceId(id),
			CraftResourceSourceKind.Synthetic,
			id,
			capability);
		return new CraftResourceSourceSnapshot(
			descriptor,
			resources.Select(resource => new KeyValuePair<int, int>(resource.ItemType, resource.Count)));
	}

	private static string SnapshotSignature(CraftResourceSourceSnapshot snapshot) =>
		$"{snapshot.Descriptor.SourceId}|{string.Join(',', snapshot.ResourceCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}";

	private static string Signature(CraftExecutionPreflightResult result) => string.Join('|',
		result.Status,
		string.Join(',', result.StepPreview.SelectMany(step => step.Demands)
			.Select(demand => $"{demand.StepSequence}:{demand.IngredientOrdinal}:{demand.ItemType}:{demand.TransactionLocalQuantity}:{demand.ExternalSourceQuantity}")),
		string.Join(',', result.AllocationPreview.Select(allocation =>
			$"{allocation.StepSequence}:{allocation.IngredientOrdinal}:{allocation.ItemType}:{allocation.SourceId}:{allocation.Quantity}")),
		string.Join(',', result.Issues.Select(issue => issue.Kind)));

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"M5.1 execution preflight synthetic validation failed: {message}");
	}
}
