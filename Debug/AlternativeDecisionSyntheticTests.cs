using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using Builder = GraphCraft.Debug.RecursivePlannerSyntheticTests.SyntheticGraphBuilder;
using Fixture = GraphCraft.Debug.RecursivePlannerSyntheticTests.Fixture;
using Spec = GraphCraft.Debug.RecursivePlannerSyntheticTests.IngredientSpec;

namespace GraphCraft.Debug;

internal sealed record AlternativeDecisionSyntheticReport(
	int PassedRequiredTestCount,
	RecursiveRecipePlanResult ChoiceExample,
	RecursiveRecipePlanResult ExplicitChoiceExample,
	TimeSpan Elapsed);

internal static class AlternativeDecisionSyntheticTests
{
	private const int RootOutput = 9000;

	public static AlternativeDecisionSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		TestOneViableOneFailedAutoSelects();
		RecursiveRecipePlanResult choice = TestTwoViableRequireChoice();
		TestThreeViableRequireChoice();
		TestFailedBranchReservationIsolation();
		TestFailedBranchProductionIsolation();
		RecursiveRecipePlanResult explicitChoice = TestExplicitChoiceSelectsSecond();
		TestInvalidExplicitChoice();
		TestExplicitChoiceFailureDoesNotFallback();
		TestCanonicalCandidateOrderIgnoresInsertionOrder();
		TestBlockedPlusSuccessAutoSelects();
		TestRecursivelyViableCandidate();
		TestReadyNowUsesEvolvedLedger();
		TestLocalAlternativesRemainChoiceBeforeLaterSibling();
		TestExplicitBadLocalChoiceDoesNotFallbackAfterLaterFailure();
		TestExplicitGoodLocalChoiceSucceeds();
		TestNestedDecisionProvenance();
		TestViablePlusNestedChoiceAutoSelects();
		TestViablePlusRecipeGroupDeferredAutoSelects();
		TestFailedAndBlockedPreserveFrontiers();
		TestDeferredAndBlockedIsNotImpossible();
		TestUniqueProducerRegression();
		TestZeroProducerRegression();
		TestSnapshotAndPreviewImmutability();
		TestDecisionDeterminism();
		TestTwoViablePlusDeferredRequiresChoice();
		timer.Stop();
		return new AlternativeDecisionSyntheticReport(25, choice, explicitChoice, timer.Elapsed);
	}

	private static void TestOneViableOneFailedAutoSelects()
	{
		var b = Builder();
		RecipeNode good = Add(b, 10, 700, I(701));
		Add(b, 20, 700, I(702));
		RecipeNode root = Add(b, 30, RootOutput, I(700));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((701, 1))));
		Assert(result.Succeeded && ReferenceEquals(result.AppliedChoices.Single().SelectedRecipe, good), "Test 1 did not auto-select the only viable candidate.");
		Assert(result.Decisions.Single().State == AlternativeDecisionState.AutoSelectedOnlyViable, "Test 1 decision state mismatch.");
	}

	private static RecursiveRecipePlanResult TestTwoViableRequireChoice()
	{
		var b = Builder();
		Add(b, 10, 710, I(711));
		Add(b, 20, 710, I(712));
		RecipeNode root = Add(b, 30, RootOutput + 1, I(710));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((711, 1), (712, 1))));
		AlternativeDecision decision = RequiredDecision(result);
		Assert(result.Status == RecursivePlanStatus.UserChoiceRequired && decision.FullyViableCandidateCount == 2,
			"Test 2 did not expose two viable candidates.");
		Assert(decision.UserSelectableCandidates.Count == 2 && decision.UserSelectableCandidates.All(candidate => candidate.Status == AlternativeCandidateStatus.FullyViable),
			"Test 2 exposed a non-viable candidate as a user selection.");
		Assert(result.Steps.Count == 0 && decision.Candidates.All(candidate => candidate.PreviewSteps.Count == 1),
			"Test 2 mixed previews with committed steps.");
		return result;
	}

	private static void TestThreeViableRequireChoice()
	{
		var b = Builder();
		Add(b, 30, 720, I(723));
		Add(b, 10, 720, I(721));
		Add(b, 20, 720, I(722));
		RecipeNode root = Add(b, 40, RootOutput + 2, I(720));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context((721, 1), (722, 1), (723, 1)))));
		Assert(decision.FullyViableCandidateCount == 3 && decision.Candidates.Select(c => c.Recipe.RecipeIndex).SequenceEqual(new[] { 10, 20, 30 }),
			"Test 3 did not preserve canonical order for three candidates.");
	}

	private static void TestFailedBranchReservationIsolation()
	{
		var b = Builder();
		Add(b, 10, 730, I(731), I(732));
		RecipeNode good = Add(b, 20, 730, I(731));
		RecipeNode root = Add(b, 30, RootOutput + 3, I(730));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((731, 1))));
		Assert(result.Succeeded && ReferenceEquals(result.AppliedChoices.Single().SelectedRecipe, good), "Test 4 failed branch reservation leaked into the next candidate.");
		Assert(result.ResultLedger.GetBalance(731).ReservedQuantity == 1, "Test 4 committed a failed candidate reservation.");
	}

	private static void TestFailedBranchProductionIsolation()
	{
		var b = Builder();
		RecipeNode child = Add(b, 5, 741, I(742));
		Add(b, 10, 740, I(741), I(743));
		RecipeNode good = Add(b, 20, 740, I(744));
		RecipeNode root = Add(b, 30, RootOutput + 4, I(740));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((742, 1), (744, 1))));
		Assert(result.Succeeded && ReferenceEquals(result.AppliedChoices.Single().SelectedRecipe, good), "Test 5 did not select the independent viable branch.");
		Assert(result.Steps.All(step => !ReferenceEquals(step.Recipe, child)) && result.ResultLedger.GetBalance(741).PlannedProductionQuantity == 0,
			"Test 5 leaked virtual production from the failed candidate.");
	}

	private static RecursiveRecipePlanResult TestExplicitChoiceSelectsSecond()
	{
		ChoiceFixture f = TwoChoiceFixture(750, RootOutput + 5);
		RecursiveRecipePlanResult initial = Plan(f.Fixture);
		AlternativeDecision decision = RequiredDecision(initial);
		RecursiveRecipePlanResult result = Plan(f.Fixture, new ExplicitRecipeChoice(decision.DecisionId, f.Second));
		Assert(result.Succeeded && result.AppliedChoices.Any(choice => choice.Kind == RecipeChoiceApplicationKind.ExplicitUserChoice && ReferenceEquals(choice.SelectedRecipe, f.Second)),
			"Test 6 did not deterministically apply the explicit second candidate.");
		Assert(result.Steps.Any(step => ReferenceEquals(step.Recipe, f.Second)) && result.Steps.All(step => !ReferenceEquals(step.Recipe, f.First)),
			"Test 6 committed the wrong candidate steps.");
		return result;
	}

	private static void TestInvalidExplicitChoice()
	{
		ChoiceFixture f = TwoChoiceFixture(760, RootOutput + 6);
		AlternativeDecision decision = RequiredDecision(Plan(f.Fixture));
		RecursiveRecipePlanResult result = Plan(f.Fixture, new ExplicitRecipeChoice(decision.DecisionId, f.Fixture.Root));
		Assert(result.Status == RecursivePlanStatus.InvalidExplicitChoice && result.PendingDecision?.State == AlternativeDecisionState.InvalidExplicitChoice,
			"Test 7 did not reject a recipe outside the decision candidate set.");
		Assert(result.Frontier?.Kind == PlanningFrontierKind.InvalidExplicitChoice, "Test 7 did not return structured invalid-choice evidence.");
	}

	private static void TestExplicitChoiceFailureDoesNotFallback()
	{
		var b = Builder();
		RecipeNode first = Add(b, 10, 770, I(771));
		RecipeNode second = Add(b, 20, 770, I(772));
		RecipeNode root = Add(b, 30, RootOutput + 7, I(770));
		var viable = b.Build(root, Context((771, 1), (772, 1)));
		AlternativeDecision decision = RequiredDecision(Plan(viable));
		var changed = b.Build(root, Context((771, 1)));
		RecursiveRecipePlanResult result = Plan(changed, new ExplicitRecipeChoice(decision.DecisionId, second));
		Assert(result.Status == RecursivePlanStatus.ExplicitChoiceFailed && result.AppliedChoices.Single().SelectedRecipe == second,
			"Test 8 did not preserve explicit-choice failure.");
		Assert(result.Steps.Count == 0 && result.AppliedChoices.All(choice => !ReferenceEquals(choice.SelectedRecipe, first)), "Test 8 silently fell back to the viable first candidate.");
	}

	private static void TestCanonicalCandidateOrderIgnoresInsertionOrder()
	{
		(string id, string candidates) first = OrderSignature(reverse: false);
		(string id, string candidates) second = OrderSignature(reverse: true);
		Assert(first == second && first.candidates == "10:FullyViable,20:FullyViable", "Test 9 candidate order depended on graph insertion order.");
	}

	private static void TestBlockedPlusSuccessAutoSelects()
	{
		var b = Builder();
		RecipeNode good = Add(b, 10, 790, I(791));
		Add(b, 20, 790, RecipeLocalState.BlockedByStation, I(792));
		RecipeNode root = Add(b, 30, RootOutput + 9, I(790));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((791, 1), (792, 1))));
		Assert(result.Succeeded && ReferenceEquals(result.AppliedChoices.Single().SelectedRecipe, good), "Test 10 did not auto-select success after proving the other candidate blocked.");
	}

	private static void TestRecursivelyViableCandidate()
	{
		var b = Builder();
		RecipeNode child = Add(b, 5, 801, I(802));
		RecipeNode recursive = Add(b, 10, 800, I(801));
		Add(b, 20, 800, I(803));
		RecipeNode root = Add(b, 30, RootOutput + 10, I(800));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((802, 1))));
		Assert(result.Succeeded && result.Steps.Select(s => s.Recipe).SequenceEqual(new[] { child, recursive, root }), "Test 11 did not evaluate recursive candidate viability.");
	}

	private static void TestReadyNowUsesEvolvedLedger()
	{
		var b = Builder();
		Add(b, 5, 811, I(810, 5));
		Add(b, 10, 812, RecipeLocalState.ReadyNow, I(810, 5));
		Add(b, 20, 812, RecipeLocalState.ReadyNow, I(810, 5));
		RecipeNode root = Add(b, 30, RootOutput + 11, I(811), I(812));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((810, 7))));
		Assert(result.Status == RecursivePlanStatus.NoViableAlternative && result.PendingDecision?.Candidates.All(c => c.Frontier?.AvailableQuantity == 2) == true,
			"Test 12 allowed stale ReadyNow evidence to override the evolved Ledger.");
	}

	private static void TestLocalAlternativesRemainChoiceBeforeLaterSibling()
	{
		LocalTradeoffFixture f = LocalTradeoff();
		RecursiveRecipePlanResult result = Plan(f.Fixture);
		Assert(result.Status == RecursivePlanStatus.UserChoiceRequired && RequiredDecision(result).FullyViableCandidateCount == 2,
			"Test 13 performed forbidden global lookahead/backtracking instead of exposing the local choice.");
	}

	private static void TestExplicitBadLocalChoiceDoesNotFallbackAfterLaterFailure()
	{
		LocalTradeoffFixture f = LocalTradeoff();
		AlternativeDecision decision = RequiredDecision(Plan(f.Fixture));
		RecursiveRecipePlanResult result = Plan(f.Fixture, new ExplicitRecipeChoice(decision.DecisionId, f.IronBranch));
		Assert(result.Status == RecursivePlanStatus.InsufficientTerminalResource && result.AppliedChoices.Single().SelectedRecipe == f.IronBranch,
			"Test 14 did not preserve the explicit local choice through a later sibling failure.");
		Assert(result.Steps.Count == 0 && result.AppliedChoices.All(choice => !ReferenceEquals(choice.SelectedRecipe, f.LeadBranch)), "Test 14 silently fell back after later failure.");
	}

	private static void TestExplicitGoodLocalChoiceSucceeds()
	{
		LocalTradeoffFixture f = LocalTradeoff();
		AlternativeDecision decision = RequiredDecision(Plan(f.Fixture));
		RecursiveRecipePlanResult result = Plan(f.Fixture, new ExplicitRecipeChoice(decision.DecisionId, f.LeadBranch));
		Assert(result.Succeeded && result.Steps.Any(step => ReferenceEquals(step.Recipe, f.LeadBranch)), "Test 15 explicit resource-preserving branch did not succeed.");
	}

	private static void TestNestedDecisionProvenance()
	{
		var b = Builder();
		Add(b, 1, 831, I(832));
		Add(b, 2, 831, I(833));
		RecipeNode nested = Add(b, 10, 830, I(831));
		Add(b, 20, 830, I(834));
		RecipeNode root = Add(b, 30, RootOutput + 15, I(830));
		AlternativeDecision outer = RequiredDecision(Plan(b.Build(root, Context((832, 1), (833, 1)))));
		Assert(outer.FullyViableCandidateCount == 0 && outer.State == AlternativeDecisionState.UserChoiceRequired && outer.UserSelectableCandidates.Count == 0 &&
			outer.Candidates.Any(candidate => candidate.Status == AlternativeCandidateStatus.RequiresUserChoice) &&
			outer.Candidates.Any(candidate => candidate.Status == AlternativeCandidateStatus.Failed),
			"Test 16 did not propagate the required nested choice when no fully viable outer candidate existed.");
		AlternativeCandidateResult nestedCandidate = outer.Candidates.Single(c => ReferenceEquals(c.Recipe, nested));
		AlternativeDecision nestedDecision = nestedCandidate.NestedDecision
			?? throw new InvalidOperationException("Test 16 expected nested decision provenance.");
		Assert(nestedCandidate.Status == AlternativeCandidateStatus.RequiresUserChoice && nestedDecision.ConsumingRecipe == nested,
			"Test 16 did not preserve nested decision provenance on the candidate.");
		Assert(nestedCandidate.Decisions.Single().DecisionId == nestedDecision.DecisionId,
			"Test 16 did not retain the nested decision in candidate provenance.");
	}

	private static void TestViablePlusNestedChoiceAutoSelects()
	{
		var b = Builder();
		Add(b, 1, 841, I(842));
		Add(b, 2, 841, I(843));
		RecipeNode viable = Add(b, 10, 840, I(844));
		RecipeNode nested = Add(b, 20, 840, I(841));
		RecipeNode root = Add(b, 30, RootOutput + 16, I(840));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((842, 1), (843, 1), (844, 1))));
		AlternativeDecision decision = result.Decisions.Single(candidateDecision => ReferenceEquals(candidateDecision.ConsumingRecipe, root));
		Assert(result.Succeeded && decision.State == AlternativeDecisionState.AutoSelectedOnlyViable && ReferenceEquals(decision.SelectedRecipe, viable),
			"Test 17 did not auto-select the unique fully viable candidate.");
		Assert(decision.Candidates.Any(c => c.Recipe == nested && c.Status == AlternativeCandidateStatus.RequiresUserChoice), "Test 17 nested-choice classification mismatch.");
	}

	private static void TestViablePlusRecipeGroupDeferredAutoSelects()
	{
		var b = Builder();
		RecipeNode viable = Add(b, 10, 850, I(851));
		Add(b, 20, 850, new Spec(852, 1, true));
		RecipeNode root = Add(b, 30, RootOutput + 17, I(850));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((851, 1), (852, 1))));
		AlternativeDecision decision = result.Decisions.Single();
		Assert(result.Succeeded && decision.State == AlternativeDecisionState.AutoSelectedOnlyViable && ReferenceEquals(decision.SelectedRecipe, viable),
			"Test 18 did not auto-select the unique fully viable candidate beside a deferred candidate.");
		Assert(decision.Candidates.Any(c => c.Status == AlternativeCandidateStatus.Deferred),
			"Test 18 did not retain Recipe Group deferral in decision provenance.");
	}

	private static void TestFailedAndBlockedPreserveFrontiers()
	{
		var b = Builder();
		Add(b, 10, 860, I(861));
		Add(b, 20, 860, RecipeLocalState.BlockedByStation, I(862));
		RecipeNode root = Add(b, 30, RootOutput + 18, I(860));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context())));
		Assert(decision.State == AlternativeDecisionState.NoViableCandidate &&
			decision.Candidates.Select(candidate => candidate.Status).OrderBy(status => status).SequenceEqual(
				new[] { AlternativeCandidateStatus.Failed, AlternativeCandidateStatus.Blocked }.OrderBy(status => status)) &&
			decision.Candidates.All(c => c.Frontier is not null),
			"Test 19 did not preserve failed/blocked candidate frontiers and no-viable semantics.");
	}

	private static void TestDeferredAndBlockedIsNotImpossible()
	{
		var b = Builder();
		Add(b, 10, 870, new Spec(871, 1, true));
		Add(b, 20, 870, RecipeLocalState.BlockedByCondition, I(872));
		RecipeNode root = Add(b, 30, RootOutput + 19, I(870));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((871, 1), (872, 1))));
		Assert(result.Status == RecursivePlanStatus.AlternativeResolutionDeferred && result.PendingDecision?.State == AlternativeDecisionState.UnresolvedDeferred,
			"Test 20 collapsed deferred/blocked candidates into impossible.");
	}

	private static void TestUniqueProducerRegression()
	{
		var b = Builder();
		RecipeNode child = Add(b, 10, 880, I(881));
		RecipeNode root = Add(b, 20, RootOutput + 20, I(880));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context((881, 1))));
		Assert(result.Succeeded && result.Decisions.Count == 0 && result.Steps.First().Recipe == child, "Test 21 regressed unique-producer planning.");
	}

	private static void TestZeroProducerRegression()
	{
		var b = Builder();
		RecipeNode root = Add(b, 10, RootOutput + 21, I(891));
		RecursiveRecipePlanResult result = Plan(b.Build(root, Context()));
		Assert(result.Status == RecursivePlanStatus.InsufficientTerminalResource && result.Decisions.Count == 0, "Test 22 regressed zero-producer frontier behavior.");
	}

	private static void TestSnapshotAndPreviewImmutability()
	{
		var source = new CraftResourceSourceSnapshot(
			new CraftResourceSourceDescriptor(
				new CraftResourceSourceId("m43:test23"),
				CraftResourceSourceKind.Synthetic,
				"m43:test23",
				CraftResourceConsumptionCapability.EvidenceOnly),
			new Dictionary<int, int> { [901] = 1, [902] = 1 });
		PlayerCraftContextSnapshot context = PlayerCraftContextSnapshot.CreateSynthetic(new[] { source });
		var b = Builder();
		Add(b, 10, 900, I(901));
		Add(b, 20, 900, I(902));
		RecipeNode root = Add(b, 30, RootOutput + 22, I(900));
		var fixture = b.Build(root, context);
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(context.Resources.GetAvailableCount(901) == 1 && source.GetCount(901) == 1 && fixture.Ledger.ReservedCounts.Count == 0,
			"Test 23 mutated source, aggregate, or input Ledger snapshots.");
		Assert(result.Steps.Count == 0 && result.PendingDecision?.Candidates.All(c => c.PreviewSteps.Count == 1 && c.ResultLedger is not null) == true,
			"Test 23 did not keep candidate previews separate and immutable from committed output.");
		AlternativeDecision decision = RequiredDecision(result);
		RecursiveRecipePlanResult selected = Plan(fixture, new ExplicitRecipeChoice(decision.DecisionId, decision.Candidates[1].Recipe));
		Assert(selected.Succeeded && context.Resources.GetAvailableCount(902) == 1 && source.GetCount(902) == 1 && fixture.Ledger.ReservedCounts.Count == 0,
			"Test 23 explicit deterministic re-plan mutated source, aggregate, or input Ledger snapshots.");
	}

	private static void TestDecisionDeterminism()
	{
		ChoiceFixture f = TwoChoiceFixture(910, RootOutput + 23);
		RecursiveRecipePlanResult first = Plan(f.Fixture);
		RecursiveRecipePlanResult second = Plan(f.Fixture);
		Assert(DecisionSignature(first) == DecisionSignature(second), "Test 24 decision identity/order/status/previews were not deterministic.");
	}

	private static void TestTwoViablePlusDeferredRequiresChoice()
	{
		var b = Builder();
		Add(b, 10, 920, I(921));
		Add(b, 20, 920, I(922));
		Add(b, 30, 920, new Spec(923, 1, true));
		RecipeNode root = Add(b, 40, RootOutput + 24, I(920));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context((921, 1), (922, 1), (923, 1)))));
		Assert(decision.State == AlternativeDecisionState.UserChoiceRequired && decision.Candidates.Count == 3 &&
			decision.UserSelectableCandidates.Select(candidate => candidate.Recipe.RecipeIndex).SequenceEqual(new[] { 10, 20 }),
			"Test 25 did not restrict the user-facing choice set to the two fully viable candidates.");
		Assert(decision.Candidates.Single(candidate => candidate.Recipe.RecipeIndex == 30).Status == AlternativeCandidateStatus.Deferred,
			"Test 25 did not preserve the deferred candidate in decision provenance.");
	}

	private static ChoiceFixture TwoChoiceFixture(int item, int rootItem)
	{
		var b = Builder();
		RecipeNode first = Add(b, 10, item, I(item + 1));
		RecipeNode second = Add(b, 20, item, I(item + 2));
		RecipeNode root = Add(b, 30, rootItem, I(item));
		return new ChoiceFixture(b.Build(root, Context((item + 1, 1), (item + 2, 1))), first, second);
	}

	private static LocalTradeoffFixture LocalTradeoff()
	{
		var b = Builder();
		RecipeNode iron = Add(b, 10, 820, I(821, 10));
		RecipeNode lead = Add(b, 20, 820, I(822, 10));
		Add(b, 25, 823, I(821, 10));
		RecipeNode root = Add(b, 30, RootOutput + 12, I(820), I(823));
		return new LocalTradeoffFixture(b.Build(root, Context((821, 10), (822, 10))), iron, lead);
	}

	private static (string id, string candidates) OrderSignature(bool reverse)
	{
		var b = Builder();
		if (reverse) {
			Add(b, 20, 780, I(782));
			Add(b, 10, 780, I(781));
		}
		else {
			Add(b, 10, 780, I(781));
			Add(b, 20, 780, I(782));
		}
		RecipeNode root = Add(b, 30, RootOutput + 8, I(780));
		AlternativeDecision decision = RequiredDecision(Plan(b.Build(root, Context((781, 1), (782, 1)))));
		return (decision.DecisionId.Value, string.Join(',', decision.Candidates.Select(c => $"{c.Recipe.RecipeIndex}:{c.Status}")));
	}

	private static string DecisionSignature(RecursiveRecipePlanResult result)
	{
		AlternativeDecision decision = RequiredDecision(result);
		return $"{decision.DecisionId}|{decision.State}|" + string.Join(';', decision.Candidates.Select(candidate =>
			$"{candidate.Recipe.RecipeIndex}:{candidate.Status}:{candidate.ResultStatus}:{string.Join(',', candidate.PreviewSteps.Select(step => step.Recipe.RecipeIndex))}"));
	}

	private static AlternativeDecision RequiredDecision(RecursiveRecipePlanResult result) =>
		result.PendingDecision ?? throw new InvalidOperationException("Expected an unresolved alternative decision.");

	private static RecursiveRecipePlanResult Plan(Fixture fixture, params ExplicitRecipeChoice[] choices) =>
		RecursiveSinglePathPlanner.Plan(new RecursiveRecipePlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Root,
			requiredOutputQuantity: 1,
			fixture.Ledger,
			new RecursivePlanningLimits(),
			choices));

	private static Builder Builder() => new();
	private static Spec I(int item, int amount = 1) => new(item, amount, false);
	private static PlayerCraftContextSnapshot Context(params (int ItemType, int Count)[] resources) => RecursivePlannerSyntheticTests.Context(resources);

	private static RecipeNode Add(Builder builder, int index, int result, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, 1, RecipeLocalState.NeedsIngredients, included: true, ingredients);

	private static RecipeNode Add(Builder builder, int index, int result, RecipeLocalState state, params Spec[] ingredients) =>
		builder.AddRecipeWithIndex(index, result, 1, state, included: true, ingredients);

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"Alternative decision synthetic validation failed: {message}");
	}

	private sealed record ChoiceFixture(Fixture Fixture, RecipeNode First, RecipeNode Second);
	private sealed record LocalTradeoffFixture(Fixture Fixture, RecipeNode IronBranch, RecipeNode LeadBranch);
}
