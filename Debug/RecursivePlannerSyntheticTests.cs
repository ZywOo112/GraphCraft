using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Context;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Planning;
using GraphCraft.Core.Resolution;
using GraphCraft.Core.Resources;
using GraphCraft.Core.Subgraphs;

namespace GraphCraft.Debug;

internal sealed record RecursivePlannerSyntheticReport(
	int PassedRequiredTestCount,
	bool TerminalAndTruncationBoundariesPassed,
	RecursiveRecipePlanResult OneLevelExample,
	RecursiveRecipePlanResult FrontierExample,
	TimeSpan Elapsed);

internal static class RecursivePlannerSyntheticTests
{
	public static RecursivePlannerSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		RecursiveRecipePlanResult oneLevel = TestOneLevelRecursiveSuccess();
		TestMultiLevelRecursiveSuccess();
		TestExistingPartialIntermediateProducesDeficit();
		TestIntermediateSurplusReuse();
		TestSharedBaseResourceCannotDoubleCount();
		TestRecursiveFailureAtomicity();
		TestZeroProducerFrontier();
		RecursiveRecipePlanResult frontier = TestMultipleProducersDeferred();
		TestMultipleProducersWithOneReadyStillDeferred();
		TestRecipeGroupConcreteAllocation();
		TestNeedsIngredientsIsRecursivelyResolvable();
		TestReadyNowDoesNotOverrideLedger();
		TestBlockedByStationPropagation();
		TestBlockedByConditionPropagation();
		TestUnknownConstraintPropagation();
		TestSimpleCycleBoundary();
		TestCycleDetectionIsPathLocal();
		TestDepthLimit();
		TestMultiOutputIntermediate();
		TestSnapshotImmutability();
		TestGraphConstraint();
		TestDeterminism();
		timer.Stop();
		return new RecursivePlannerSyntheticReport(
			PassedRequiredTestCount: 22,
			TerminalAndTruncationBoundariesPassed: true,
			oneLevel,
			frontier,
			timer.Elapsed);
	}

	private static RecursiveRecipePlanResult TestOneLevelRecursiveSuccess()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(302, 1, RecipeLocalState.ReadyNow, included: true, I(301, 3));
		RecipeNode makeT = builder.AddRecipe(303, 1, RecipeLocalState.NeedsIngredients, included: true, I(302, 2));
		Fixture fixture = builder.Build(makeT, Context((301, 6)));
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(result.Succeeded, "Test 1 one-level recursive plan failed.");
		AssertSteps(result, makeB, makeT);
		Assert(result.Steps[0].CraftRepetitions == 2 && result.Steps[0].ProducedQuantity == 2,
			"Test 1 child quantity propagation mismatch.");
		Assert(Balance(result, 301) == new VirtualResourceBalance(301, 6, 0, 6, 0),
			"Test 1 base resource reservation mismatch.");
		Assert(Balance(result, 302) == new VirtualResourceBalance(302, 0, 2, 2, 0),
			"Test 1 intermediate production/reservation mismatch.");
		Assert(Balance(result, 303).PlannedProductionQuantity == 1, "Test 1 did not credit root output.");
		return result;
	}

	private static void TestMultiLevelRecursiveSuccess()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(312, 1, RecipeLocalState.ReadyNow, true, I(311, 1));
		RecipeNode makeC = builder.AddRecipe(313, 1, RecipeLocalState.NeedsIngredients, true, I(312, 1));
		RecipeNode makeT = builder.AddRecipe(314, 1, RecipeLocalState.NeedsIngredients, true, I(313, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(makeT, Context((311, 1))));
		Assert(result.Succeeded, "Test 2 multi-level recursion failed.");
		AssertSteps(result, makeB, makeC, makeT);
		Assert(Balance(result, 311).RemainingQuantity == 0 && Balance(result, 314).RemainingQuantity == 1,
			"Test 2 final Ledger mismatch.");
	}

	private static void TestExistingPartialIntermediateProducesDeficit()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(322, 4, RecipeLocalState.ReadyNow, true, I(321, 3));
		RecipeNode makeT = builder.AddRecipe(323, 1, RecipeLocalState.NeedsIngredients, true, I(322, 5));
		RecursiveRecipePlanResult result = Plan(builder.Build(makeT, Context((321, 3), (322, 2))));
		Assert(result.Succeeded && result.Steps.Count == 2, "Test 3 partial intermediate plan failed.");
		Assert(result.Steps[0].Recipe == makeB && result.Steps[0].RequestedOutputQuantity == 3 &&
			result.Steps[0].CraftRepetitions == 1 && result.Steps[0].ProducedQuantity == 4,
			"Test 3 did not request only the deficit.");
		Assert(Balance(result, 322) == new VirtualResourceBalance(322, 2, 4, 5, 1),
			"Test 3 did not preserve one surplus B.");
	}

	private static void TestIntermediateSurplusReuse()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(332, 5, RecipeLocalState.ReadyNow, true, I(331, 3));
		RecipeNode makeT = builder.AddRecipe(333, 1, RecipeLocalState.NeedsIngredients, true, I(332, 3), I(332, 2));
		RecursiveRecipePlanResult result = Plan(builder.Build(makeT, Context((331, 3))));
		Assert(result.Succeeded, "Test 4 surplus reuse plan failed.");
		Assert(result.Steps.Count(step => ReferenceEquals(step.Recipe, makeB)) == 1,
			"Test 4 crafted the intermediate a second time instead of reusing surplus.");
		Assert(Balance(result, 332) == new VirtualResourceBalance(332, 0, 5, 5, 0),
			"Test 4 sibling reservations did not share virtual B.");
	}

	private static void TestSharedBaseResourceCannotDoubleCount()
	{
		Fixture fixture = SharedIronFailureFixture();
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(!result.Succeeded && result.Status == RecursivePlanStatus.InsufficientTerminalResource,
			"Test 5 double-counted shared Iron across recursive siblings.");
		Assert(result.Frontier?.Item.ItemType == 341 && result.Frontier.AvailableQuantity == 2 && result.Frontier.DeficitQuantity == 3,
			"Test 5 frontier did not use the evolved Iron remainder.");
		Assert(ReferenceEquals(result.InputLedger, result.ResultLedger), "Test 5 exposed a changed root Ledger.");
	}

	private static void TestRecursiveFailureAtomicity()
	{
		Fixture fixture = SharedIronFailureFixture();
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(!result.Succeeded && ReferenceEquals(result.InputLedger, result.ResultLedger),
			"Test 6 recursive failure did not return the root input Ledger.");
		Assert(result.Steps.Count == 0 && result.Reservations.Count == 0 && result.Productions.Count == 0,
			"Test 6 exposed child state from a failed root attempt.");
		Assert(result.ResultLedger.PlannedProductionCounts.Count == 0 && result.ResultLedger.ReservedCounts.Count == 0,
			"Test 6 left virtual production or reservations in the committed Ledger.");
	}

	private static void TestZeroProducerFrontier()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(352, 1, RecipeLocalState.ReadyNow, included: false, I(351, 1));
		RecipeNode root = builder.AddRecipe(353, 1, RecipeLocalState.NeedsIngredients, included: true, I(352, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((351, 10))));
		Assert(result.Status == RecursivePlanStatus.NoProducerInTargetSubgraph &&
			result.Frontier?.Kind == PlanningFrontierKind.NoProducerInTargetSubgraph,
			"Test 7 did not return NoProducerInTargetSubgraph.");

		var terminalBuilder = new SyntheticGraphBuilder();
		RecipeNode terminalRoot = terminalBuilder.AddRecipe(355, 1, RecipeLocalState.NeedsIngredients, true, I(354, 1));
		Assert(Plan(terminalBuilder.Build(terminalRoot, Context())).Status == RecursivePlanStatus.InsufficientTerminalResource,
			"Test 7 additional atomic evidence boundary was not preserved.");

		var truncatedBuilder = new SyntheticGraphBuilder();
		truncatedBuilder.AddRecipe(357, 1, RecipeLocalState.ReadyNow, false, I(356, 1));
		RecipeNode truncatedRoot = truncatedBuilder.AddRecipe(358, 1, RecipeLocalState.NeedsIngredients, true, I(357, 1));
		Assert(Plan(truncatedBuilder.Build(truncatedRoot, Context(), truncated: true)).Status == RecursivePlanStatus.TargetSubgraphTruncated,
			"Test 7 additional truncation boundary was not preserved.");
	}

	private static RecursiveRecipePlanResult TestMultipleProducersDeferred()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(362, 1, RecipeLocalState.ReadyNow, true, I(361, 1));
		builder.AddRecipe(362, 1, RecipeLocalState.ReadyNow, true, I(363, 1));
		RecipeNode root = builder.AddRecipe(364, 1, RecipeLocalState.NeedsIngredients, true, I(362, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((361, 1), (363, 1))));
		Assert(result.Status == RecursivePlanStatus.UserChoiceRequired && result.PendingDecision?.Candidates.Count == 2,
			"Test 8 did not expose the two viable producers as a user decision.");
		Assert(result.Steps.Count == 0 && result.Productions.Count == 0, "Test 8 committed a candidate preview.");
		return result;
	}

	private static void TestMultipleProducersWithOneReadyStillDeferred()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(372, 1, RecipeLocalState.ReadyNow, true, I(371, 1));
		builder.AddRecipe(372, 1, RecipeLocalState.BlockedByStation, true, I(373, 1));
		RecipeNode root = builder.AddRecipe(374, 1, RecipeLocalState.NeedsIngredients, true, I(372, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((371, 1), (373, 1))));
		Assert(result.Succeeded && result.AppliedChoices.Single().Kind == RecipeChoiceApplicationKind.AutoSelectedOnlyViable,
			"Test 9 did not auto-select the only fully viable producer after proving the other blocked.");
	}

	private static void TestRecipeGroupConcreteAllocation()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(382, 1, RecipeLocalState.NeedsIngredients, true, I(381, 1, group: true));
		RecipeNode root = builder.AddRecipe(383, 1, RecipeLocalState.NeedsIngredients, true, I(382, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((381, 20))));
		Assert(result.Succeeded && result.GroupAllocations.Count == 1 &&
			result.GroupAllocations[0].ConcreteAllocations.Single().ItemType == 381,
			"Test 10 did not concretely allocate the Recipe Group requirement.");
	}

	private static void TestNeedsIngredientsIsRecursivelyResolvable()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(392, 1, RecipeLocalState.ReadyNow, true, I(391, 2));
		RecipeNode root = builder.AddRecipe(393, 1, RecipeLocalState.NeedsIngredients, true, I(392, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((391, 2))));
		Assert(result.Succeeded, "Test 11 treated NeedsIngredients as a permanent block.");
	}

	private static void TestReadyNowDoesNotOverrideLedger()
	{
		Fixture fixture = SharedIronFailureFixture(childState: RecipeLocalState.ReadyNow);
		RecipeNode secondChild = fixture.Root.Ingredients[1].Source.ProducerRecipes.Single();
		Assert(fixture.Resolution.GetRecipeResolution(secondChild).State == RecipeLocalState.ReadyNow,
			"Test 12 fixture did not preserve ReadyNow evidence.");
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(result.Status == RecursivePlanStatus.InsufficientTerminalResource && result.Frontier?.AvailableQuantity == 2,
			"Test 12 allowed stale ReadyNow to override the evolved Ledger.");
	}

	private static void TestBlockedByStationPropagation() => AssertExternalBlock(
		RecipeLocalState.BlockedByStation,
		RecursivePlanStatus.BlockedByStation,
		PlanningFrontierKind.BlockedByStation,
		"Test 13");

	private static void TestBlockedByConditionPropagation() => AssertExternalBlock(
		RecipeLocalState.BlockedByCondition,
		RecursivePlanStatus.BlockedByCondition,
		PlanningFrontierKind.BlockedByCondition,
		"Test 14");

	private static void TestUnknownConstraintPropagation() => AssertExternalBlock(
		RecipeLocalState.UnknownConstraint,
		RecursivePlanStatus.UnknownConstraint,
		PlanningFrontierKind.UnknownConstraint,
		"Test 15");

	private static void TestSimpleCycleBoundary()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeA = builder.AddRecipe(421, 1, RecipeLocalState.NeedsIngredients, true, I(422, 1));
		builder.AddRecipe(422, 1, RecipeLocalState.NeedsIngredients, true, I(421, 1));
		RecipeNode root = builder.AddRecipe(423, 1, RecipeLocalState.NeedsIngredients, true, I(421, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context()));
		Assert(result.Status == RecursivePlanStatus.CycleBoundary && result.Frontier?.Kind == PlanningFrontierKind.CycleBoundary,
			"Test 16 did not stop the simple cycle.");
		Assert(result.Frontier!.DependencyPath.Count(entry => ReferenceEquals(entry.Recipe, makeA)) == 2,
			"Test 16 frontier did not preserve the repeated path identity.");
	}

	private static void TestCycleDetectionIsPathLocal()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(432, 1, RecipeLocalState.ReadyNow, true, I(431, 1));
		RecipeNode root = builder.AddRecipe(433, 1, RecipeLocalState.NeedsIngredients, true, I(432, 1), I(432, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((431, 2))));
		Assert(result.Succeeded && result.Steps.Count(step => ReferenceEquals(step.Recipe, makeB)) == 2,
			"Test 17 treated sibling re-entry as a cycle.");
	}

	private static void TestDepthLimit()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(442, 1, RecipeLocalState.ReadyNow, true, I(441, 1));
		builder.AddRecipe(443, 1, RecipeLocalState.NeedsIngredients, true, I(442, 1));
		builder.AddRecipe(444, 1, RecipeLocalState.NeedsIngredients, true, I(443, 1));
		RecipeNode root = builder.AddRecipe(445, 1, RecipeLocalState.NeedsIngredients, true, I(444, 1));
		Fixture fixture = builder.Build(root, Context((441, 1)));
		RecursiveRecipePlanResult result = Plan(fixture, maximumDepth: 2);
		Assert(result.Status == RecursivePlanStatus.PlanningDepthBoundary && result.Frontier?.Depth == 3,
			"Test 18 did not return the configured planning depth boundary.");
	}

	private static void TestMultiOutputIntermediate()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode makeB = builder.AddRecipe(452, 5, RecipeLocalState.ReadyNow, true, I(451, 2));
		RecipeNode root = builder.AddRecipe(453, 1, RecipeLocalState.NeedsIngredients, true, I(452, 12));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((451, 6))));
		Assert(result.Succeeded && result.Steps[0].Recipe == makeB && result.Steps[0].CraftRepetitions == 3 &&
			result.Steps[0].Ingredients.Single().RequiredQuantity == 6 && result.Steps[0].ProducedQuantity == 15,
			"Test 19 multi-output quantity propagation mismatch.");
		Assert(Balance(result, 452) == new VirtualResourceBalance(452, 0, 15, 12, 3),
			"Test 19 did not preserve three surplus B.");
	}

	private static void TestSnapshotImmutability()
	{
		CraftResourceSourceSnapshot sourceA = Source("m42:test20:a", (461, 3));
		CraftResourceSourceSnapshot sourceB = Source("m42:test20:b", (461, 3));
		PlayerCraftContextSnapshot context = PlayerCraftContextSnapshot.CreateSynthetic(new[] { sourceA, sourceB });
		CraftResourceAggregateSnapshot aggregate = context.Resources;
		CraftResourceEvidence[] provenance = aggregate.GetEvidence(461).ToArray();
		var successBuilder = new SyntheticGraphBuilder();
		successBuilder.AddRecipe(462, 1, RecipeLocalState.ReadyNow, true, I(461, 3));
		RecipeNode successRoot = successBuilder.AddRecipe(463, 1, RecipeLocalState.NeedsIngredients, true, I(462, 2));
		Fixture successFixture = successBuilder.Build(successRoot, context);
		ResolutionSnapshot resolution = successFixture.Resolution;
		Assert(Plan(successFixture).Succeeded, "Test 20 success fixture failed.");

		var failureBuilder = new SyntheticGraphBuilder();
		failureBuilder.AddRecipe(465, 1, RecipeLocalState.ReadyNow, true, I(461, 3));
		RecipeNode failureRoot = failureBuilder.AddRecipe(466, 1, RecipeLocalState.NeedsIngredients, true, I(465, 1), I(464, 1));
		Assert(!Plan(failureBuilder.Build(failureRoot, context)).Succeeded, "Test 20 failure fixture unexpectedly succeeded.");
		Assert(ReferenceEquals(aggregate, context.Resources) && aggregate.GetAvailableCount(461) == 6,
			"Test 20 mutated the aggregate snapshot.");
		Assert(sourceA.GetCount(461) == 3 && sourceB.GetCount(461) == 3 && provenance.SequenceEqual(aggregate.GetEvidence(461)),
			"Test 20 mutated source snapshots or provenance.");
		Assert(ReferenceEquals(resolution.Context.Resources, aggregate) && resolution.RecipeResolutions.Count == successFixture.Subgraph.RecipeNodes.Count,
			"Test 20 mutated or replaced ResolutionSnapshot evidence.");
	}

	private static void TestGraphConstraint()
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode outside = builder.AddRecipe(472, 100, RecipeLocalState.ReadyNow, included: false, I(471, 1));
		RecipeNode root = builder.AddRecipe(473, 1, RecipeLocalState.NeedsIngredients, included: true, I(472, 1));
		RecursiveRecipePlanResult result = Plan(builder.Build(root, Context((471, 1))));
		Assert(result.Status == RecursivePlanStatus.NoProducerInTargetSubgraph &&
			result.Steps.All(step => !ReferenceEquals(step.Recipe, outside)) &&
			(result.Frontier?.CandidateProducers.Count ?? -1) == 0,
			"Test 21 used the convenient graph-external producer.");
	}

	private static void TestDeterminism()
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(482, 3, RecipeLocalState.ReadyNow, true, I(481, 2));
		builder.AddRecipe(483, 1, RecipeLocalState.ReadyNow, true, I(484, 1));
		RecipeNode root = builder.AddRecipe(485, 2, RecipeLocalState.NeedsIngredients, true, I(482, 4), I(483, 1));
		Fixture fixture = builder.Build(root, Context((481, 6), (484, 2)));
		RecursiveRecipePlanResult first = Plan(fixture, requiredOutput: 3);
		RecursiveRecipePlanResult second = Plan(fixture, requiredOutput: 3);
		Assert(Signature(first) == Signature(second), "Test 22 recursive results were not deterministic.");
	}

	private static void AssertExternalBlock(
		RecipeLocalState childState,
		RecursivePlanStatus expectedStatus,
		PlanningFrontierKind expectedKind,
		string test)
	{
		var builder = new SyntheticGraphBuilder();
		RecipeNode child = builder.AddRecipe(402, 1, childState, true, I(401, 1));
		RecipeNode root = builder.AddRecipe(403, 1, RecipeLocalState.NeedsIngredients, true, I(402, 1));
		Fixture fixture = builder.Build(root, Context((401, 1)));
		RecipeResolution evidence = fixture.Resolution.GetRecipeResolution(child);
		RecursiveRecipePlanResult result = Plan(fixture);
		Assert(result.Status == expectedStatus && result.Frontier?.Kind == expectedKind &&
			ReferenceEquals(result.Frontier.ResolutionEvidence, evidence),
			$"{test} did not propagate external Resolution evidence.");
		Assert(ReferenceEquals(result.InputLedger, result.ResultLedger) && result.Steps.Count == 0,
			$"{test} committed state despite the external block.");
	}

	private static Fixture SharedIronFailureFixture(RecipeLocalState childState = RecipeLocalState.NeedsIngredients)
	{
		var builder = new SyntheticGraphBuilder();
		builder.AddRecipe(342, 1, childState, true, I(341, 5));
		builder.AddRecipe(343, 1, childState, true, I(341, 5));
		RecipeNode root = builder.AddRecipe(344, 1, RecipeLocalState.NeedsIngredients, true, I(342, 1), I(343, 1));
		return builder.Build(root, Context((341, 7)));
	}

	private static RecursiveRecipePlanResult Plan(Fixture fixture, int requiredOutput = 1, int maximumDepth = 64) =>
		RecursiveSinglePathPlanner.Plan(new RecursiveRecipePlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Root,
			requiredOutput,
			fixture.Ledger,
			new RecursivePlanningLimits(maximumDepth)));

	private static VirtualResourceBalance Balance(RecursiveRecipePlanResult result, int itemType) =>
		result.ResultLedger.GetBalance(itemType);

	private static IngredientSpec I(int itemType, int amount, bool group = false) => new(itemType, amount, group);

	internal static PlayerCraftContextSnapshot Context(params (int ItemType, int Count)[] resources) =>
		PlayerCraftContextSnapshot.CreateSynthetic(resources.Select(resource =>
			new KeyValuePair<int, int>(resource.ItemType, resource.Count)));

	private static CraftResourceSourceSnapshot Source(string id, params (int ItemType, int Count)[] resources) =>
		new(
			new CraftResourceSourceDescriptor(
				new CraftResourceSourceId(id),
				CraftResourceSourceKind.Synthetic,
				id,
				CraftResourceConsumptionCapability.EvidenceOnly),
			resources.Select(resource => new KeyValuePair<int, int>(resource.ItemType, resource.Count)));

	private static void AssertSteps(RecursiveRecipePlanResult result, params RecipeNode[] expected) =>
		Assert(result.Steps.Select(step => step.Recipe).SequenceEqual(expected) &&
			result.Steps.Select(step => step.Sequence).SequenceEqual(Enumerable.Range(0, expected.Length)),
			"Planned steps are not in deterministic producer-before-consumer order.");

	private static string Signature(RecursiveRecipePlanResult result)
	{
		string frontier = result.Frontier is null
			? "none"
			: string.Join(':',
				result.Frontier.Kind,
				result.Frontier.Item.ItemType,
				result.Frontier.RequiredQuantity,
				result.Frontier.AvailableQuantity,
				result.Frontier.DeficitQuantity,
				string.Join(',', result.Frontier.DependencyPath.Select(entry => entry.Recipe.RecipeIndex)),
				string.Join(',', result.Frontier.CandidateProducers.Select(recipe => recipe.RecipeIndex)));
		return string.Join('|',
			result.Status,
			frontier,
			string.Join(',', result.Steps.Select(step =>
				$"{step.Sequence}:{step.Recipe.RecipeIndex}:{step.CraftRepetitions}:{step.ProducedQuantity}")),
			string.Join(',', result.Reservations.Select(reservation =>
				$"{reservation.ItemType}:{reservation.Quantity}:{reservation.RemainingAfter}")),
			string.Join(',', result.Productions.Select(production =>
				$"{production.ItemType}:{production.Quantity}:{production.AvailableAfter}")),
			string.Join(',', result.ResultLedger.ReservedCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
			string.Join(',', result.ResultLedger.PlannedProductionCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"Recursive planner synthetic validation failed: {message}");
	}

	internal readonly record struct IngredientSpec(int ItemType, int Amount, bool IsGroup);

	internal sealed record Fixture(
		TargetCraftSubgraph Subgraph,
		ResolutionSnapshot Resolution,
		RecipeNode Root,
		VirtualResourceLedger Ledger);

	internal sealed class SyntheticGraphBuilder
	{
		private static int _nextRecipeIndex = -42000;
		private readonly Dictionary<int, ItemNode> _items = new();
		private readonly List<RecipeNode> _includedRecipes = new();
		private readonly Dictionary<RecipeNode, RecipeLocalState> _states = new();

		public RecipeNode AddRecipe(
			int resultItemType,
			int outputStack,
			RecipeLocalState state,
			bool included,
			params IngredientSpec[] ingredients)
		{
			return AddRecipeCore(_nextRecipeIndex--, resultItemType, outputStack, state, included, ingredients);
		}

		public RecipeNode AddRecipeWithIndex(
			int recipeIndex,
			int resultItemType,
			int outputStack,
			RecipeLocalState state,
			bool included,
			params IngredientSpec[] ingredients) =>
			AddRecipeCore(recipeIndex, resultItemType, outputStack, state, included, ingredients);

		private RecipeNode AddRecipeCore(
			int recipeIndex,
			int resultItemType,
			int outputStack,
			RecipeLocalState state,
			bool included,
			IReadOnlyList<IngredientSpec> ingredients)
		{
			ItemNode result = Item(resultItemType);
			var recipe = new RecipeNode(
				recipeIndex,
				result,
				outputStack,
				new RecipeConstraintSet(Array.Empty<int>(), Array.Empty<RecipeConditionInfo>()));
			result.AddProducer(recipe);
			for (int ordinal = 0; ordinal < ingredients.Count; ordinal++) {
				IngredientSpec spec = ingredients[ordinal];
				ItemNode source = Item(spec.ItemType);
				IReadOnlyList<RecipeGroupInfo> groups = spec.IsGroup
					? new[] { new RecipeGroupInfo(8000 + ordinal, spec.ItemType, new[] { spec.ItemType, spec.ItemType + 10000 }, "Synthetic Group") }
					: Array.Empty<RecipeGroupInfo>();
				if (spec.IsGroup)
					Item(spec.ItemType + 10000);
				var edge = new IngredientEdge(ordinal, source, recipe, spec.Amount, groups);
				recipe.AddIngredient(edge);
				source.AddConsumer(recipe);
			}
			if (included) {
				_includedRecipes.Add(recipe);
				_states.Add(recipe, state);
			}
			return recipe;
		}

		public Fixture Build(
			RecipeNode root,
			PlayerCraftContextSnapshot context,
			bool truncated = false)
		{
			RecipeNode[] recipes = _includedRecipes.ToArray();
			IngredientEdge[] edges = recipes.SelectMany(recipe => recipe.Ingredients).ToArray();
			ItemNode[] items = _items.Values.ToArray();
			var depths = new Dictionary<GraphNodeKey, int>();
			foreach (ItemNode item in items)
				depths[GraphNodeKey.From(item)] = ReferenceEquals(item, root.Result) ? 0 : 1;
			foreach (RecipeNode recipe in recipes)
				depths[GraphNodeKey.From(recipe)] = ReferenceEquals(recipe, root) ? 0 : 1;
			var subgraph = new TargetCraftSubgraph(
				root.Result,
				items,
				recipes,
				edges,
				recipes.Select(recipe => recipe.Product),
				depths,
				includedComponentIds: new[] { 1 },
				TargetSubgraphOptions.Bounded(8, 500),
				truncated ? SubgraphTruncationReason.DepthLimit : SubgraphTruncationReason.None,
				skippedFrontierCount: truncated ? 1 : 0,
				alternativeProducerRecipeCount: items.Sum(item => Math.Max(0, item.ProducerRecipes.Count(recipes.Contains) - 1)),
				sharedItemNodeCount: 0,
				TimeSpan.Zero);

			var itemResolutions = items.ToDictionary(
				item => item,
				item => new ItemResolution(
					item,
					context.GetAvailableCount(item.ItemType) > 0
						? ItemResolutionState.Owned
						: item.Classification == ItemClassification.Atomic ? ItemResolutionState.UnownedAtomic : ItemResolutionState.UnownedDerived,
					context.GetAvailableCount(item.ItemType),
					ComponentId: 1,
					IsCyclic: false));
			var ingredientResolutions = edges.ToDictionary(
				edge => edge,
				edge => {
					int[] types = edge.AcceptsRecipeGroup
						? edge.AcceptedRecipeGroups.SelectMany(group => group.AcceptedItemTypes).Distinct().ToArray()
						: new[] { edge.Source.ItemType };
					int available = types.Sum(context.GetAvailableCount);
					return new IngredientResolution(
						edge,
						edge.RequiredAmount,
						available,
						available >= edge.RequiredAmount,
						edge.AcceptsRecipeGroup ? IngredientResolutionMode.RecipeGroup : IngredientResolutionMode.DirectItem,
						types.SelectMany(context.GetResourceEvidence).ToArray());
				});
			var recipeResolutions = recipes.ToDictionary(
				recipe => recipe,
				recipe => CreateRecipeResolution(recipe, _states[recipe], ingredientResolutions));
			var resolution = new ResolutionSnapshot(
				subgraph,
				context,
				itemResolutions,
				ingredientResolutions,
				recipeResolutions,
				TimeSpan.Zero);
			return new Fixture(subgraph, resolution, root, VirtualResourceLedger.Create(context.Resources));
		}

		private ItemNode Item(int itemType)
		{
			if (!_items.TryGetValue(itemType, out ItemNode? item)) {
				item = new ItemNode(itemType);
				_items.Add(itemType, item);
			}
			return item;
		}

		private static RecipeResolution CreateRecipeResolution(
			RecipeNode recipe,
			RecipeLocalState state,
			IReadOnlyDictionary<IngredientEdge, IngredientResolution> ingredients)
		{
			StationConstraintResolution station = state == RecipeLocalState.BlockedByStation
				? new StationConstraintResolution(StationConstraintState.Unsatisfied, new[] { 18 }, new[] { 18 })
				: new StationConstraintResolution(StationConstraintState.Satisfied, Array.Empty<int>(), Array.Empty<int>());
			IReadOnlyList<ConditionConstraintResolution> conditions = state switch
			{
				RecipeLocalState.BlockedByCondition => new[] {
					new ConditionConstraintResolution(new RecipeConditionKey(recipe.RecipeIndex, 0), "Synthetic false", ConditionConstraintState.Unsatisfied, null)
				},
				RecipeLocalState.UnknownConstraint => new[] {
					new ConditionConstraintResolution(new RecipeConditionKey(recipe.RecipeIndex, 0), "Synthetic unknown", ConditionConstraintState.Unknown, "Synthetic evidence")
				},
				_ => Array.Empty<ConditionConstraintResolution>()
			};
			return new RecipeResolution(
				recipe,
				state,
				recipe.Ingredients.Select(ingredient => ingredients[ingredient]).ToArray(),
				station,
				conditions,
				ComponentId: 1,
				IsCyclic: false);
		}
	}
}
