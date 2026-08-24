using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using Builder = GraphCraft.Debug.RecursivePlannerSyntheticTests.SyntheticGraphBuilder;
using Fixture = GraphCraft.Debug.RecursivePlannerSyntheticTests.Fixture;
using Spec = GraphCraft.Debug.RecursivePlannerSyntheticTests.IngredientSpec;

namespace GraphCraft.Debug;

internal sealed record ResourceAlternativeSyntheticReport(
	int PassedRequiredTestCount,
	RecursiveRecipePlanResult BacktrackingExample,
	RecursiveRecipePlanResult GroupAllocationExample,
	RecursiveRecipePlanResult BudgetExample,
	TimeSpan Elapsed);

internal static class ResourceAlternativeSyntheticTests
{
	public static ResourceAlternativeSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		RecursiveRecipePlanResult backtracking = TestClassicEarlierRecipeBacktracking();
		TestTwoRootSuccessfulPathsRequireChoice();
		TestExplicitRecipeChoicePreventsBacktracking();
		RecursiveRecipePlanResult group = TestBasicGroupSplit();
		TestGroupBacktrackingSavesLaterExact();
		TestMixedAllocationAndLaterExact();
		TestMultipleGroupsAreAutomaticAndDeterministic();
		TestRecipeOrAndGroupInteraction();
		TestThreeLevelNestedBacktracking();
		TestAllPlannerBranchesFailAtomically();
		TestDeferredIsNotImpossible();
		TestOneSuccessPlusDeferredAutoSelects();
		TestTwoSuccessPlusDeferredChoiceSet();
		TestRecursiveProductionForGroupMember();
		TestGroupAllocationPreservesVirtualSurplus();
		TestGroupAndExactCannotDoubleCount();
		RecursiveRecipePlanResult budget = TestSearchBudgetBoundary();
		TestDeterminism();
		TestExplicitRecipeWithInternalResourceBacktracking();
		TestSequentialUserChoicePoints();
		timer.Stop();
		return new ResourceAlternativeSyntheticReport(20, backtracking, group, budget, timer.Elapsed);
	}

	private static RecursiveRecipePlanResult TestClassicEarlierRecipeBacktracking()
	{
		var b = new Builder();
		RecipeNode iron = Add(b, 10, 1000, I(1001, 10));
		RecipeNode lead = Add(b, 20, 1000, I(1002, 10));
		Add(b, 25, 1003, I(1001, 10));
		RecipeNode root = Add(b, 30, 1004, I(1000), I(1003));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1001, 10), (1002, 10))));
		Assert(result.Succeeded && result.Decisions.Single().SelectedRecipe == lead && result.Steps.All(step => step.Recipe != iron),
			"Test 1 did not backtrack the earlier recipe choice to preserve the later exact resource.");
		return result;
	}

	private static void TestTwoRootSuccessfulPathsRequireChoice()
	{
		var b = new Builder();
		Add(b, 10, 1010, I(1011));
		Add(b, 20, 1010, I(1012));
		RecipeNode root = Add(b, 30, 1013, I(1010));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context((1011, 1), (1012, 1)))));
		Assert(decision.FullyViableCandidateCount == 2 && decision.UserSelectableCandidates.Count == 2,
			"Test 2 did not expose exactly the two complete root-successful recipes.");
	}

	private static void TestExplicitRecipeChoicePreventsBacktracking()
	{
		var b = new Builder();
		RecipeNode bad = Add(b, 10, 1020, I(1021, 5));
		Add(b, 20, 1020, I(1022, 5));
		Add(b, 25, 1023, I(1021, 5));
		RecipeNode root = Add(b, 30, 1024, I(1020), I(1023));
		Fixture fixture = b.Build(root, Context((1021, 5), (1022, 5)));
		AlternativeDecision automatic = Plan(fixture).Decisions.Single();
		RecursiveRecipePlanResult result = Plan(fixture, choices: new[] { new ExplicitRecipeChoice(automatic.DecisionId, bad) });
		Assert(result.Status == RecursivePlanStatus.ExplicitChoiceFailed && result.Decisions.Single().SelectedRecipe == bad,
			"Test 3 backtracked across an explicit recipe constraint.");
	}

	private static RecursiveRecipePlanResult TestBasicGroupSplit()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 1030, G(1031, 10));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1031, 4), (11031, 6))));
		RecipeGroupAllocation allocation = result.GroupAllocations.Single();
		Assert(result.Succeeded && Allocation(allocation, 1031) == 4 && Allocation(allocation, 11031) == 6,
			"Test 4 did not split a Recipe Group requirement 4+6 across concrete ItemTypes.");
		return result;
	}

	private static void TestGroupBacktrackingSavesLaterExact()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 1040, G(1041, 6), I(1041, 6));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1041, 6), (11041, 6))));
		Assert(result.Succeeded && Allocation(result.GroupAllocations.Single(), 11041) == 6 && result.ResultLedger.GetBalance(1041).ReservedQuantity == 6,
			"Test 5 did not backtrack group allocation to save Wood for a later exact requirement.");
	}

	private static void TestMixedAllocationAndLaterExact()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 1050, G(1051, 10), I(1051, 2));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1051, 6), (11051, 6))));
		RecipeGroupAllocation allocation = result.GroupAllocations.Single();
		Assert(result.Succeeded && Allocation(allocation, 1051) == 4 && Allocation(allocation, 11051) == 6,
			"Test 6 did not find the root-consistent mixed 4+6 allocation while preserving two exact Wood.");
	}

	private static void TestMultipleGroupsAreAutomaticAndDeterministic()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 1060, G(1061, 2), G(1062, 3));
		Fixture fixture = b.Build(root, Context((1061, 1), (11061, 1), (1062, 2), (11062, 1)));
		RecursiveRecipePlanResult first = Plan(fixture);
		RecursiveRecipePlanResult second = Plan(fixture);
		Assert(first.Succeeded && first.PendingDecision is null && first.GroupAllocations.Count == 2 && Signature(first) == Signature(second),
			"Test 7 exposed routine group allocation as user choice or made it nondeterministic.");
	}

	private static void TestRecipeOrAndGroupInteraction()
	{
		var b = new Builder();
		Add(b, 10, 1070, G(1071));
		RecipeNode preserving = Add(b, 20, 1070, I(1072));
		RecipeNode root = Add(b, 30, 1073, I(1070), I(1071));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1071, 1), (1072, 1))));
		Assert(result.Succeeded && result.Decisions.Single().SelectedRecipe == preserving,
			"Test 8 did not combine Recipe OR search with concrete group allocation continuation.");
	}

	private static void TestThreeLevelNestedBacktracking()
	{
		var b = new Builder();
		Add(b, 10, 1080, I(1081, 4));
		RecipeNode preserve = Add(b, 20, 1080, I(1082, 4));
		Add(b, 30, 1083, I(1080));
		Add(b, 40, 1084, I(1083), I(1081, 4));
		RecipeNode root = Add(b, 50, 1085, I(1084));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1081, 4), (1082, 4))));
		Assert(result.Succeeded && result.Decisions.Single().SelectedRecipe == preserve,
			"Test 9 did not propagate a three-level continuation failure back to the earlier branch.");
	}

	private static void TestAllPlannerBranchesFailAtomically()
	{
		var b = new Builder();
		Add(b, 10, 1090, I(1091, 2));
		Add(b, 20, 1090, I(1092, 2));
		RecipeNode root = Add(b, 30, 1093, I(1090));
		Fixture fixture = b.Build(root, Context((1091, 1), (1092, 1)));
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(!result.Succeeded && result.Status == RecursivePlanStatus.NoViableAlternative && ReferenceEquals(result.InputLedger, result.ResultLedger) && result.Steps.Count == 0,
			"Test 10 committed state after exhausting all planner-owned branches.");
	}

	private static void TestDeferredIsNotImpossible()
	{
		var b = new Builder();
		Add(b, 10, 1110, RecipeLocalState.UnknownConstraint, I(1111));
		Add(b, 20, 1110, I(1112, 2));
		RecipeNode root = Add(b, 30, 1113, I(1110));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1112, 1))));
		Assert(result.Status == RecursivePlanStatus.AlternativeResolutionDeferred && result.Decisions.Single().State == AlternativeDecisionState.UnresolvedDeferred,
			"Test 11 collapsed deferred evidence into impossibility.");
	}

	private static void TestOneSuccessPlusDeferredAutoSelects()
	{
		var b = new Builder();
		RecipeNode success = Add(b, 10, 1120, I(1121));
		Add(b, 20, 1120, RecipeLocalState.UnknownConstraint, I(1122));
		RecipeNode root = Add(b, 30, 1123, I(1120));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1121, 1))));
		Assert(result.Succeeded && result.Decisions.Single().SelectedRecipe == success,
			"Test 12 let a deferred candidate block the unique complete route.");
	}

	private static void TestTwoSuccessPlusDeferredChoiceSet()
	{
		var b = new Builder();
		Add(b, 10, 1130, I(1131));
		Add(b, 20, 1130, I(1132));
		Add(b, 30, 1130, RecipeLocalState.UnknownConstraint, I(1133));
		RecipeNode root = Add(b, 40, 1134, I(1130));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context((1131, 1), (1132, 1)))));
		Assert(decision.UserSelectableCandidates.Select(candidate => candidate.Recipe.RecipeIndex).SequenceEqual(new[] { 10, 20 }) &&
			decision.Candidates.Single(candidate => candidate.Recipe.RecipeIndex == 30).Status == AlternativeCandidateStatus.Deferred,
			"Test 13 did not restrict user candidates to two root-successful recipes.");
	}

	private static void TestRecursiveProductionForGroupMember()
	{
		var b = new Builder();
		RecipeNode producer = Add(b, 10, 1141 + 10000, 5, I(1142, 2));
		RecipeNode root = Add(b, 20, 1140, G(1141, 3));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1142, 2))));
		Assert(result.Succeeded && result.Steps.First().Recipe == producer && Allocation(result.GroupAllocations.Single(), 11141) == 3,
			"Test 14 did not recursively produce an accepted concrete group member inside the subgraph.");
	}

	private static void TestGroupAllocationPreservesVirtualSurplus()
	{
		var b = new Builder();
		Add(b, 10, 1151 + 10000, 5, I(1152));
		RecipeNode root = Add(b, 20, 1150, G(1151, 3));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1152, 1))));
		Assert(result.Succeeded && result.ResultLedger.GetBalance(11151).RemainingQuantity == 2,
			"Test 15 did not preserve virtual surplus after concrete group reservation.");
	}

	private static void TestGroupAndExactCannotDoubleCount()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 1160, G(1161, 4), I(1161, 3));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1161, 6))));
		Assert(!result.Succeeded && ReferenceEquals(result.InputLedger, result.ResultLedger),
			"Test 16 double-counted the same concrete item between group and exact requirements.");
	}

	private static RecursiveRecipePlanResult TestSearchBudgetBoundary()
	{
		var b = new Builder();
		Add(b, 10, 1170, I(1171, 2));
		Add(b, 20, 1170, I(1172));
		RecipeNode root = Add(b, 30, 1173, I(1170));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((1171, 1), (1172, 1))), maximumBranches: 1);
		Assert(result.Status == RecursivePlanStatus.SearchBudgetBoundary && result.SearchStatistics.BudgetExhausted && result.Steps.Count == 0,
			"Test 17 did not return the structured search-budget boundary.");
		return result;
	}

	private static void TestDeterminism()
	{
		var b = new Builder();
		Add(b, 10, 1180, I(1181));
		Add(b, 20, 1180, I(1182));
		RecipeNode root = Add(b, 30, 1183, I(1180), G(1184, 2));
		Fixture fixture = b.Build(root, Context((1181, 1), (1182, 1), (1184, 1), (11184, 1)));
		Assert(Signature(Plan(fixture)) == Signature(Plan(fixture)), "Test 18 root-continuation search was not deterministic.");
	}

	private static void TestExplicitRecipeWithInternalResourceBacktracking()
	{
		var b = new Builder();
		RecipeNode groupRecipe = Add(b, 10, 1190, G(1191), I(1191));
		Add(b, 20, 1190, I(1192));
		RecipeNode root = Add(b, 30, 1193, I(1190));
		Fixture fixture = b.Build(root, Context((1191, 1), (11191, 1), (1192, 1)));
		AlternativeDecision decision = RequiredDecision(Plan(fixture));
		RecursiveRecipePlanResult result = Plan(fixture, choices: new[] { new ExplicitRecipeChoice(decision.DecisionId, groupRecipe) });
		Assert(result.Succeeded && result.Decisions.Single().State == AlternativeDecisionState.ExplicitlySelected &&
			Allocation(result.GroupAllocations.Single(), 11191) == 1,
			"Test 19 either relaxed explicit recipe choice or failed to backtrack its internal resource allocation.");
	}

	private static void TestSequentialUserChoicePoints()
	{
		var b = new Builder();
		Add(b, 10, 1200, I(1201));
		Add(b, 20, 1200, I(1202));
		Add(b, 30, 1203, I(1204));
		Add(b, 40, 1203, I(1205));
		RecipeNode root = Add(b, 50, 1206, I(1200), I(1203));
		Fixture fixture = b.Build(root, Context((1201, 1), (1202, 1), (1204, 1), (1205, 1)));
		RecursiveRecipePlanResult first = Plan(fixture);
		AlternativeDecision firstDecision = RequiredDecision(first);
		RecursiveRecipePlanResult second = Plan(fixture, choices: new[] { new ExplicitRecipeChoice(firstDecision.DecisionId, firstDecision.UserSelectableCandidates[0].Recipe) });
		Assert(second.Status == RecursivePlanStatus.UserChoiceRequired && RequiredDecision(second).DecisionId != firstDecision.DecisionId &&
			second.Decisions.Count(decision => decision.State == AlternativeDecisionState.UserChoiceRequired) == 1,
			"Test 20 did not expose user choices sequentially, one actionable decision per request.");
	}

	private static RecipeNode Add(Builder builder, int index, int result, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, 1, RecipeLocalState.NeedsIngredients, true, ingredients);
	private static RecipeNode Add(Builder builder, int index, int result, RecipeLocalState state, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, 1, state, true, ingredients);
	private static RecipeNode Add(Builder builder, int index, int result, int output, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, output, RecipeLocalState.NeedsIngredients, true, ingredients);
	private static Spec I(int item, int amount = 1) => new(item, amount, false);
	private static Spec G(int iconicItem, int amount = 1) => new(iconicItem, amount, true);
	private static PlayerCraftContextSnapshot Context(params (int ItemType, int Count)[] resources) => RecursivePlannerSyntheticTests.Context(resources);

	private static RecursiveRecipePlanResult Plan(Fixture fixture, int maximumBranches = RecursivePlanningLimits.DefaultMaximumBranchExpansions,
		IEnumerable<ExplicitRecipeChoice>? choices = null) =>
		GraphConstrainedCraftPlanner.Plan(new RecursiveRecipePlanningRequest(fixture.Subgraph, fixture.Resolution, fixture.Root, 1, fixture.Ledger,
			new RecursivePlanningLimits(maximumBranchExpansions: maximumBranches), choices));

	private static AlternativeDecision RequiredDecision(RecursiveRecipePlanResult result) =>
		result.PendingDecision ?? throw new InvalidOperationException("Expected an actionable recipe decision.");
	private static long Allocation(RecipeGroupAllocation allocation, int itemType) =>
		allocation.ConcreteAllocations.Where(value => value.ItemType == itemType).Sum(value => value.Quantity);
	private static string Signature(RecursiveRecipePlanResult result) => string.Join('|',
		result.Status,
		string.Join(',', result.Steps.Select(step => step.Recipe.RecipeIndex)),
		string.Join(';', result.Decisions.Select(decision => $"{decision.DecisionId}:{decision.State}:{decision.SelectedRecipe?.RecipeIndex}")),
		string.Join(';', result.GroupAllocations.Select(allocation => allocation.DeterministicSignature)),
		$"{result.SearchStatistics.ExpandedBranches}:{result.SearchStatistics.BudgetExhausted}");
	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"M4.4 resource-alternative synthetic validation failed: {message}");
	}
}
