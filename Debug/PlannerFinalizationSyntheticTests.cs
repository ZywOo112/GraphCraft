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

internal sealed record PlannerFinalizationSyntheticReport(
	int TargetPlannerTestCount,
	int CycleTestCount,
	int TruncationTestCount,
	int SearchBudgetTestCount,
	TargetCraftPlanResult TargetChoiceExample,
	TargetCraftPlanResult CycleRecoveryExample,
	TargetCraftPlanResult BudgetExample,
	TimeSpan Elapsed);

internal static class PlannerFinalizationSyntheticTests
{
	public static PlannerFinalizationSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		TestTargetOneRootSuccess();
		TestTargetOnlyOneRootSuccessfulAutoSelects();
		TargetCraftPlanResult choice = TestTargetTwoRootSuccessfulRequireChoice();
		TestExplicitRootChoiceUsesSelectedRecipe();
		TestExplicitRootChoiceFailureDoesNotFallback();
		TestTargetZeroProducerBoundary();
		TestTargetTruncatedRootUncertainty();
		TestAdditionalQuantityIgnoresOwnedTarget();
		TestRootMultiOutputQuantity();
		TestRootChoiceDeterminism();

		TestSimpleCycleBoundary();
		TestExistingIntermediateAvoidsCycle();
		TargetCraftPlanResult cycleRecovery = TestCyclicAndAcyclicRootCandidates();
		TestCycleBranchDoesNotPolluteLedger();
		TestCycleEvidenceDeterminism();

		TestIrrelevantTruncationWithSuccess();
		TestRelevantTruncationWithoutSuccess();
		TestSuccessfulAlternativesDespiteTruncation();
		TestTruncationNeverEscapesSubgraph();

		TargetCraftPlanResult budget = TestSmallBudgetBoundary();
		TestNormalPlanBelowBudget();
		TestBudgetFailureLeavesInputLedgerUnchanged();
		TestBudgetCandidateIsDeferredNotFailed();
		TestExplicitRootChoiceRespectedAtBudgetBoundary();
		timer.Stop();
		return new PlannerFinalizationSyntheticReport(10, 5, 4, 5, choice, cycleRecovery, budget, timer.Elapsed);
	}

	private static void TestTargetOneRootSuccess()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2000, I(2001, 2));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2001, 2))));
		Assert(result.Succeeded && result.SelectedRootRecipe == root && result.Steps.Single().Recipe == root,
			"T1 one-root target did not plan successfully.");
	}

	private static void TestTargetOnlyOneRootSuccessfulAutoSelects()
	{
		var b = new Builder();
		RecipeNode first = Add(b, 10, 2010, I(2011));
		Add(b, 20, 2010, I(2012));
		TargetCraftPlanResult result = Plan(b.Build(first, Context((2011, 1))));
		Assert(result.Succeeded && result.SelectedRootRecipe == first && result.RootDecision?.State == RootRecipeDecisionState.AutoSelectedOnlyViable,
			"T2 did not auto-select the only root-successful recipe.");
	}

	private static TargetCraftPlanResult TestTargetTwoRootSuccessfulRequireChoice()
	{
		var b = new Builder();
		RecipeNode first = Add(b, 10, 2020, I(2021));
		Add(b, 20, 2020, I(2022));
		TargetCraftPlanResult result = Plan(b.Build(first, Context((2021, 1), (2022, 1))));
		Assert(result.Status == RecursivePlanStatus.UserChoiceRequired && result.RootDecision?.FullyViableCandidateCount == 2 &&
			result.RootDecision.UserSelectableCandidates.Count == 2 && result.Steps.Count == 0,
			"T3 did not expose the two complete root-successful recipes.");
		return result;
	}

	private static void TestExplicitRootChoiceUsesSelectedRecipe()
	{
		var b = new Builder();
		RecipeNode first = Add(b, 10, 2030, I(2031));
		RecipeNode second = Add(b, 20, 2030, I(2032));
		Fixture fixture = b.Build(first, Context((2031, 1), (2032, 1)));
		RootRecipeDecision decision = RequiredRootDecision(Plan(fixture));
		TargetCraftPlanResult selected = Plan(fixture, rootChoice: new ExplicitRootRecipeChoice(decision.DecisionId, second));
		Assert(selected.Succeeded && selected.SelectedRootRecipe == second && selected.RootDecision?.State == RootRecipeDecisionState.ExplicitlySelected &&
			selected.Steps.All(step => step.Recipe != first), "T4 did not apply the explicit root recipe.");
	}

	private static void TestExplicitRootChoiceFailureDoesNotFallback()
	{
		var b = new Builder();
		RecipeNode first = Add(b, 10, 2040, I(2041));
		RecipeNode second = Add(b, 20, 2040, I(2042));
		Fixture initial = b.Build(first, Context((2041, 1), (2042, 1)));
		RootRecipeDecision decision = RequiredRootDecision(Plan(initial));
		Fixture changed = b.Build(first, Context((2041, 1)));
		TargetCraftPlanResult result = Plan(changed, rootChoice: new ExplicitRootRecipeChoice(decision.DecisionId, second));
		Assert(result.Status == RecursivePlanStatus.ExplicitChoiceFailed && result.SelectedRootRecipe == second && result.Steps.Count == 0,
			"T5 silently fell back after explicit root failure.");
	}

	private static void TestTargetZeroProducerBoundary()
	{
		var b = new Builder();
		RecipeNode outside = Add(b, 10, 2050, included: false, I(2051));
		TargetCraftPlanResult result = Plan(b.Build(outside, Context()));
		Assert(result.Status == RecursivePlanStatus.NoProducerInTargetSubgraph && result.Frontier?.Kind == TargetPlanningFrontierKind.NoProducerInTargetSubgraph,
			"T6 did not return the target-level zero-producer boundary.");
	}

	private static void TestTargetTruncatedRootUncertainty()
	{
		var b = new Builder();
		RecipeNode outside = Add(b, 10, 2060, included: false, I(2061));
		TargetCraftPlanResult result = Plan(b.Build(outside, Context(), truncated: true));
		Assert(result.Status == RecursivePlanStatus.TargetSubgraphTruncated && result.Frontier?.Kind == TargetPlanningFrontierKind.TargetSubgraphTruncated,
			"T7 misclassified truncated root uncertainty.");
	}

	private static void TestAdditionalQuantityIgnoresOwnedTarget()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2070, I(2071));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2070, 4), (2071, 10))), additional: 10);
		Assert(result.Succeeded && result.RequestedAdditionalOutputQuantity == 10 && result.Steps.Single().CraftRepetitions == 10 &&
			result.ResultLedger.GetBalance(2070).RemainingQuantity == 14,
			"T8 treated additional output quantity as desired final ownership.");
	}

	private static void TestRootMultiOutputQuantity()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2080, output: 4, included: true, I(2081, 2));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2081, 6))), additional: 10);
		Assert(result.Succeeded && result.Steps.Single().CraftRepetitions == 3 && result.Steps.Single().ProducedQuantity == 12 &&
			result.ResultLedger.GetBalance(2080).RemainingQuantity == 12, "T9 root multi-output math was incorrect.");
	}

	private static void TestRootChoiceDeterminism()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 20, 2090, I(2092));
		Add(b, 10, 2090, I(2091));
		Fixture fixture = b.Build(root, Context((2091, 1), (2092, 1)));
		Assert(Signature(Plan(fixture)) == Signature(Plan(fixture)), "T10 root decision was not deterministic.");
	}

	private static void TestSimpleCycleBoundary()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2100, I(2101));
		Add(b, 20, 2101, I(2100));
		TargetCraftPlanResult result = Plan(b.Build(root, Context()));
		Assert(result.Status == RecursivePlanStatus.CycleBoundary, "C1 simple cycle did not stop at CycleBoundary.");
	}

	private static void TestExistingIntermediateAvoidsCycle()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2110, I(2111));
		Add(b, 20, 2111, I(2110));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2111, 1))));
		Assert(result.Succeeded && result.Steps.Single().Recipe == root, "C2 entered a cycle despite an existing sufficient intermediate.");
	}

	private static TargetCraftPlanResult TestCyclicAndAcyclicRootCandidates()
	{
		var b = new Builder();
		RecipeNode cyclic = Add(b, 10, 2120, I(2121));
		RecipeNode acyclic = Add(b, 20, 2120, I(2122));
		Add(b, 30, 2121, I(2123));
		Add(b, 40, 2123, I(2121));
		TargetCraftPlanResult result = Plan(b.Build(cyclic, Context((2122, 1))));
		Assert(result.Succeeded && result.SelectedRootRecipe == acyclic && result.RootDecision?.Candidates.Single(c => c.Recipe == cyclic).Status == AlternativeCandidateStatus.Deferred,
			"C3 did not abandon the cyclic candidate and use the acyclic root route.");
		return result;
	}

	private static void TestCycleBranchDoesNotPolluteLedger()
	{
		var b = new Builder();
		RecipeNode cyclic = Add(b, 10, 2130, I(2131));
		Add(b, 20, 2130, I(2132));
		Add(b, 30, 2131, I(2133));
		Add(b, 40, 2133, I(2131));
		Fixture fixture = b.Build(cyclic, Context((2132, 1)));
		TargetCraftPlanResult result = Plan(fixture);
		Assert(result.Succeeded && fixture.Ledger.ReservedCounts.Count == 0 && result.Steps.All(step => step.Recipe.RecipeIndex != 10),
			"C4 leaked cycle-branch state into the committed plan.");
	}

	private static void TestCycleEvidenceDeterminism()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2140, I(2141));
		Add(b, 20, 2141, I(2140));
		Fixture fixture = b.Build(root, Context());
		Assert(Signature(Plan(fixture)) == Signature(Plan(fixture)), "C5 cycle evidence was not deterministic.");
	}

	private static void TestIrrelevantTruncationWithSuccess()
	{
		var b = new Builder();
		RecipeNode truncated = Add(b, 10, 2150, I(2151));
		RecipeNode success = Add(b, 20, 2150, I(2152));
		Add(b, 30, 2151, included: false, I(2153));
		TargetCraftPlanResult result = Plan(b.Build(truncated, Context((2152, 1)), truncated: true));
		Assert(result.Succeeded && result.SelectedRootRecipe == success, "TR1 let an irrelevant truncated branch block a complete route.");
	}

	private static void TestRelevantTruncationWithoutSuccess()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2160, I(2161));
		Add(b, 20, 2161, included: false, I(2162));
		TargetCraftPlanResult result = Plan(b.Build(root, Context(), truncated: true));
		Assert(result.Status == RecursivePlanStatus.TargetSubgraphTruncated, "TR2 reported relevant truncation as proven failure.");
	}

	private static void TestSuccessfulAlternativesDespiteTruncation()
	{
		var b = new Builder();
		RecipeNode first = Add(b, 10, 2170, I(2171));
		Add(b, 20, 2170, I(2172));
		Add(b, 30, 2170, I(2173));
		Add(b, 40, 2173, included: false, I(2174));
		TargetCraftPlanResult result = Plan(b.Build(first, Context((2171, 1), (2172, 1)), truncated: true));
		Assert(result.Status == RecursivePlanStatus.UserChoiceRequired && result.RootDecision?.UserSelectableCandidates.Count == 2,
			"TR3 did not preserve normal choice semantics for successful routes beside truncation.");
	}

	private static void TestTruncationNeverEscapesSubgraph()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2180, I(2181));
		RecipeNode outside = Add(b, 20, 2181, included: false, I(2182));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2182, 10)), truncated: true));
		Assert(result.Status == RecursivePlanStatus.TargetSubgraphTruncated && result.Steps.All(step => step.Recipe != outside),
			"TR4 escaped the target subgraph to rescue a truncated plan.");
	}

	private static TargetCraftPlanResult TestSmallBudgetBoundary()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2190, I(2191));
		Add(b, 20, 2190, I(2192));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2191, 1), (2192, 1))), maximumBranches: 1);
		Assert(result.Status == RecursivePlanStatus.SearchBudgetBoundary && result.SearchStatistics.BudgetExhausted,
			"B1 did not return a deterministic small-budget boundary.");
		return result;
	}

	private static void TestNormalPlanBelowBudget()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2200, I(2201));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2201, 1))));
		Assert(result.Succeeded && !result.SearchStatistics.BudgetExhausted, "B2 ordinary plan exhausted the default budget.");
	}

	private static void TestBudgetFailureLeavesInputLedgerUnchanged()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2210, I(2211));
		Add(b, 20, 2210, I(2212));
		Fixture fixture = b.Build(root, Context((2211, 1), (2212, 1)));
		TargetCraftPlanResult result = Plan(fixture, maximumBranches: 1);
		Assert(ReferenceEquals(result.InputLedger, result.ResultLedger) && fixture.Ledger.ReservedCounts.Count == 0 && result.Steps.Count == 0,
			"B3 budget exhaustion changed the input Ledger.");
	}

	private static void TestBudgetCandidateIsDeferredNotFailed()
	{
		var b = new Builder();
		RecipeNode root = Add(b, 10, 2220, I(2221));
		Add(b, 20, 2220, I(2222));
		TargetCraftPlanResult result = Plan(b.Build(root, Context((2221, 1), (2222, 1))), maximumBranches: 1);
		Assert(result.RootDecision?.Candidates.Any(candidate => candidate.ResultStatus == RecursivePlanStatus.SearchBudgetBoundary && candidate.Status == AlternativeCandidateStatus.Deferred) == true,
			"B4 classified budget-incomplete candidate as proven failed.");
	}

	private static void TestExplicitRootChoiceRespectedAtBudgetBoundary()
	{
		var b = new Builder();
		RecipeNode selected = Add(b, 10, 2230, I(2231));
		Add(b, 20, 2230, I(2232));
		Add(b, 30, 2231, I(2233, 2));
		Add(b, 40, 2231, I(2234));
		Fixture initial = b.Build(selected, Context((2232, 1), (2234, 1)));
		RootRecipeDecisionId id = new($"root-recipe-choice:target:{selected.Result.ItemType}");
		TargetCraftPlanResult result = Plan(initial, maximumBranches: 1, rootChoice: new ExplicitRootRecipeChoice(id, selected));
		Assert(result.Status == RecursivePlanStatus.SearchBudgetBoundary && result.SelectedRootRecipe == selected &&
			result.RootDecision?.SelectedRecipe == selected && result.Steps.Count == 0,
			"B5 crossed an explicit root choice after search-budget exhaustion.");
	}

	private static TargetCraftPlanResult Plan(Fixture fixture, int additional = 1,
		int maximumBranches = RecursivePlanningLimits.DefaultMaximumBranchExpansions,
		ExplicitRootRecipeChoice? rootChoice = null,
		IEnumerable<ExplicitRecipeChoice>? recipeChoices = null) =>
		TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Subgraph.Target,
			additional,
			fixture.Ledger,
			new RecursivePlanningLimits(maximumBranchExpansions: maximumBranches),
			rootChoice,
			recipeChoices));

	private static RecipeNode Add(Builder builder, int index, int result, params Spec[] ingredients) =>
		Add(builder, index, result, output: 1, included: true, ingredients);
	private static RecipeNode Add(Builder builder, int index, int result, bool included, params Spec[] ingredients) =>
		Add(builder, index, result, output: 1, included, ingredients);
	private static RecipeNode Add(Builder builder, int index, int result, int output, bool included, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, output, RecipeLocalState.NeedsIngredients, included, ingredients);
	private static Spec I(int item, int amount = 1) => new(item, amount, false);
	private static PlayerCraftContextSnapshot Context(params (int ItemType, int Count)[] resources) => RecursivePlannerSyntheticTests.Context(resources);
	private static RootRecipeDecision RequiredRootDecision(TargetCraftPlanResult result) =>
		result.RootDecision ?? throw new InvalidOperationException("Expected a root recipe decision.");
	private static string Signature(TargetCraftPlanResult result) => string.Join('|',
		result.Status,
		result.SelectedRootRecipe?.RecipeIndex,
		result.RootDecision?.State,
		string.Join(',', result.RootDecision?.Candidates.Select(candidate => $"{candidate.Recipe.RecipeIndex}:{candidate.Status}:{candidate.ResultStatus}") ?? Array.Empty<string>()),
		result.Frontier?.Kind,
		result.Frontier?.DependencyFrontier?.Kind,
		result.SearchStatistics.ExpandedBranches,
		result.SearchStatistics.RecipeBranchExpansions,
		result.SearchStatistics.ResourceAllocationBranchExpansions,
		result.SearchStatistics.BudgetExhausted,
		string.Join(',', result.Steps.Select(step => step.Recipe.RecipeIndex)));
	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"M4 finalization synthetic validation failed: {message}");
	}
}
