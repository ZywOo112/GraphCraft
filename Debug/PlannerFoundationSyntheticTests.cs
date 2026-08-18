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

internal sealed record PlannerFoundationSyntheticReport(
	int PassedRequiredTestCount,
	bool RecipeGroupBoundaryPassed,
	SingleRecipePlanResult QuantityExample,
	TimeSpan Elapsed);

internal static class PlannerFoundationSyntheticTests
{
	private static int _nextRecipeIndex = -41000;
	private static int _nextItemType = 410000;

	public static PlannerFoundationSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		TestLedgerInitialization();
		TestSuccessfulReservation();
		TestRepeatedReservationUsesRemaining();
		TestFailedReservationDoesNotCorruptState();
		TestSingleRecipeAndSuccess();
		TestSingleRecipeAndFailureIsAtomic();
		TestSharedItemCannotBeDoubleCounted();
		SingleRecipePlanResult quantityExample = TestRecipeOutputCount();
		TestExactQuantityBoundary();
		TestInputSnapshotImmutability();
		TestGraphConstraint();
		TestMissingDirectIngredientDoesNotRecurse();
		TestResolutionEvidenceStaysExternal();
		TestDeterminism();
		TestRecipeGroupAllocationIsDeferred();
		timer.Stop();
		return new PlannerFoundationSyntheticReport(
			PassedRequiredTestCount: 14,
			RecipeGroupBoundaryPassed: true,
			quantityExample,
			timer.Elapsed);
	}

	private static void TestLedgerInitialization()
	{
		PlayerCraftContextSnapshot context = SyntheticContext((101, 10));
		VirtualResourceBalance balance = VirtualResourceLedger.Create(context.Resources).GetBalance(101);
		Assert(balance == new VirtualResourceBalance(101, 10, 0, 10), "Test 1 ledger initialization mismatch.");
	}

	private static void TestSuccessfulReservation()
	{
		PlayerCraftContextSnapshot context = SyntheticContext((102, 10));
		VirtualResourceLedger initial = VirtualResourceLedger.Create(context.Resources);
		VirtualReservationAttempt attempt = initial.TryReserveExact(102, 4);
		Assert(attempt.Succeeded && attempt.Ledger.GetBalance(102) == new VirtualResourceBalance(102, 10, 4, 6),
			"Test 2 successful reservation mismatch.");
		Assert(context.Resources.GetAvailableCount(102) == 10 && initial.GetBalance(102).RemainingQuantity == 10,
			"Test 2 mutated the input resource snapshot or ledger.");
	}

	private static void TestRepeatedReservationUsesRemaining()
	{
		VirtualResourceLedger initial = VirtualResourceLedger.Create(SyntheticContext((103, 10)).Resources);
		VirtualReservationAttempt first = initial.TryReserveExact(103, 6);
		VirtualReservationAttempt second = first.Ledger.TryReserveExact(103, 5);
		Assert(first.Succeeded && !second.Succeeded && second.AvailableQuantity == 4 && second.ShortageQuantity == 1,
			"Test 3 did not use the remaining quantity for the repeated reservation.");
		Assert(ReferenceEquals(second.Ledger, first.Ledger), "Test 3 failure did not preserve the pre-attempt ledger branch.");
	}

	private static void TestFailedReservationDoesNotCorruptState()
	{
		VirtualResourceLedger initial = VirtualResourceLedger.Create(SyntheticContext((104, 5)).Resources);
		VirtualReservationAttempt attempt = initial.TryReserveExact(104, 8);
		Assert(!attempt.Succeeded && ReferenceEquals(attempt.Ledger, initial), "Test 4 failure returned a changed ledger.");
		Assert(initial.GetBalance(104) == new VirtualResourceBalance(104, 5, 0, 5), "Test 4 corrupted ledger state.");
	}

	private static void TestSingleRecipeAndSuccess()
	{
		Fixture fixture = CreateFixture(new[] { (201, 3), (202, 4) }, outputStack: 1, SyntheticContext((201, 3), (202, 4)));
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Succeeded && result.Reservations.Count == 2, "Test 5 single recipe AND did not succeed.");
		Assert(result.ResultLedger.GetBalance(201).RemainingQuantity == 0 && result.ResultLedger.GetBalance(202).RemainingQuantity == 0,
			"Test 5 did not commit both virtual reservations.");
	}

	private static void TestSingleRecipeAndFailureIsAtomic()
	{
		Fixture fixture = CreateFixture(new[] { (203, 3), (204, 4) }, outputStack: 1, SyntheticContext((203, 10), (204, 2)));
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Status == SingleRecipePlanStatus.InsufficientDirectResources, "Test 6 did not report direct resource insufficiency.");
		Assert(ReferenceEquals(result.InputLedger, result.ResultLedger) && result.Reservations.Count == 0,
			"Test 6 exposed a partial reservation after failure.");
		Assert(result.ResultLedger.GetBalance(203).ReservedQuantity == 0 &&
			result.IngredientResults[0].Status == IngredientPlanStatus.StagedThenDiscarded &&
			result.IngredientResults[1].Status == IngredientPlanStatus.Insufficient,
			"Test 6 did not expose atomic ingredient outcomes.");
	}

	private static void TestSharedItemCannotBeDoubleCounted()
	{
		Fixture fixture = CreateFixture(new[] { (205, 5), (205, 4) }, outputStack: 1, SyntheticContext((205, 7)));
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Status == SingleRecipePlanStatus.InsufficientDirectResources && result.Reservations.Count == 0,
			"Test 7 double-counted one finite resource across two logical requirements.");
		Assert(result.IngredientResults[1].AvailableBefore == 2 && result.IngredientResults[1].ShortageQuantity == 2,
			"Test 7 did not evaluate the second requirement against the trial ledger remainder.");
	}

	private static SingleRecipePlanResult TestRecipeOutputCount()
	{
		Fixture fixture = CreateFixture(new[] { (206, 2) }, outputStack: 5, SyntheticContext((206, 6)));
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 12);
		Assert(result.Succeeded && result.CraftRepetitions == 3 && result.ProducedQuantity == 15,
			"Test 8 output-stack repetition calculation mismatch.");
		Assert(result.IngredientResults.Single().RequiredQuantity == 6, "Test 8 did not scale the ingredient quantity to six.");
		return result;
	}

	private static void TestExactQuantityBoundary()
	{
		Fixture fixture = CreateFixture(new[] { (207, 2) }, outputStack: 5, SyntheticContext((207, 4)));
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 10);
		Assert(result.Succeeded && result.CraftRepetitions == 2 && result.ProducedQuantity == 10 &&
			result.IngredientResults.Single().RequiredQuantity == 4,
			"Test 9 exact output boundary mismatch.");
	}

	private static void TestInputSnapshotImmutability()
	{
		int itemType = 208;
		CraftResourceSourceSnapshot sourceA = Source("test10:a", (itemType, 3));
		CraftResourceSourceSnapshot sourceB = Source("test10:b", (itemType, 7));
		PlayerCraftContextSnapshot context = PlayerCraftContextSnapshot.CreateSynthetic(new[] { sourceA, sourceB });
		CraftResourceAggregateSnapshot aggregate = context.Resources;
		CraftResourceEvidence[] provenanceBefore = aggregate.GetEvidence(itemType).ToArray();
		Fixture fixture = CreateFixture(new[] { (itemType, 8) }, outputStack: 1, context);
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Succeeded, "Test 10 setup plan did not succeed.");
		Assert(ReferenceEquals(aggregate, context.Resources) && aggregate.GetAvailableCount(itemType) == 10,
			"Test 10 mutated or replaced the aggregate snapshot.");
		Assert(sourceA.GetCount(itemType) == 3 && sourceB.GetCount(itemType) == 7,
			"Test 10 mutated a source snapshot.");
		Assert(provenanceBefore.SequenceEqual(aggregate.GetEvidence(itemType)), "Test 10 mutated provenance evidence.");
	}

	private static void TestGraphConstraint()
	{
		Fixture fixture = CreateFixture(new[] { (209, 1) }, outputStack: 1, SyntheticContext((209, 10)));
		RecipeNode outside = new(_nextRecipeIndex--, fixture.Recipe.Result, 1,
			new RecipeConstraintSet(Array.Empty<int>(), Array.Empty<RecipeConditionInfo>()));
		SingleRecipePlanResult result = SingleRecipePlanner.Plan(new SingleRecipePlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			outside,
			requiredOutputQuantity: 1,
			fixture.Ledger));
		Assert(result.Status == SingleRecipePlanStatus.RecipeOutsideTargetSubgraph &&
			ReferenceEquals(result.InputLedger, result.ResultLedger) && result.Reservations.Count == 0,
			"Test 11 planned a recipe outside the target subgraph.");
	}

	private static void TestMissingDirectIngredientDoesNotRecurse()
	{
		Fixture fixture = CreateFixture(
			new[] { (210, 5) },
			outputStack: 1,
			SyntheticContext(),
			includeProducerForFirstIngredient: true);
		Assert(fixture.Subgraph.RecipeNodes.Count == 2, "Test 12 fixture does not contain the synthetic ingredient producer.");
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Status == SingleRecipePlanStatus.InsufficientDirectResources &&
			ReferenceEquals(result.SelectedRecipe, fixture.Recipe) && result.IngredientResults.Count == 1 && result.Reservations.Count == 0,
			"Test 12 recursively expanded a missing direct ingredient.");
	}

	private static void TestResolutionEvidenceStaysExternal()
	{
		foreach ((RecipeLocalState localState, SingleRecipePlanStatus expected) in new[] {
			(RecipeLocalState.BlockedByStation, SingleRecipePlanStatus.BlockedByStation),
			(RecipeLocalState.BlockedByCondition, SingleRecipePlanStatus.BlockedByCondition),
			(RecipeLocalState.UnknownConstraint, SingleRecipePlanStatus.UnknownConstraint)
		}) {
			Fixture fixture = CreateFixture(new[] { (211, 1) }, outputStack: 1, SyntheticContext((211, 1)), localState);
			RecipeResolution originalEvidence = fixture.Resolution.GetRecipeResolution(fixture.Recipe);
			SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
			Assert(result.Status == expected && ReferenceEquals(result.ResolutionEvidence, originalEvidence),
				"Test 13 did not propagate the existing station/condition resolution evidence.");
			Assert(ReferenceEquals(result.InputLedger, result.ResultLedger) && result.Reservations.Count == 0,
				"Test 13 reserved resources despite an external resolution block.");
		}
	}

	private static void TestDeterminism()
	{
		Fixture fixture = CreateFixture(new[] { (214, 2), (212, 1), (213, 3) }, outputStack: 2,
			SyntheticContext((212, 2), (213, 6), (214, 4)));
		SingleRecipePlanResult first = Plan(fixture, requiredOutput: 4);
		SingleRecipePlanResult second = Plan(fixture, requiredOutput: 4);
		Assert(PlanSignature(first) == PlanSignature(second), "Test 14 produced different results for identical inputs.");
		Assert(first.IngredientResults.Select(result => result.Ingredient.Ordinal).SequenceEqual(new[] { 0, 1, 2 }),
			"Test 14 ingredient order is not canonical ordinal order.");
	}

	private static void TestRecipeGroupAllocationIsDeferred()
	{
		Fixture fixture = CreateFixture(new[] { (215, 4) }, outputStack: 1, SyntheticContext((215, 20)), groupIngredientOrdinal: 0);
		SingleRecipePlanResult result = Plan(fixture, requiredOutput: 1);
		Assert(result.Status == SingleRecipePlanStatus.DeferredRecipeGroupAllocation &&
			result.IngredientResults.Single().Status == IngredientPlanStatus.DeferredRecipeGroup &&
			result.Reservations.Count == 0 && ReferenceEquals(result.InputLedger, result.ResultLedger),
			"Recipe Group allocation was not explicitly deferred at the 4.1 boundary.");
	}

	private static SingleRecipePlanResult Plan(Fixture fixture, int requiredOutput) =>
		SingleRecipePlanner.Plan(new SingleRecipePlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Recipe,
			requiredOutput,
			fixture.Ledger));

	private static Fixture CreateFixture(
		IReadOnlyList<(int ItemType, int Amount)> ingredients,
		int outputStack,
		PlayerCraftContextSnapshot context,
		RecipeLocalState localState = RecipeLocalState.ReadyNow,
		bool includeProducerForFirstIngredient = false,
		int? groupIngredientOrdinal = null)
	{
		var constraints = new RecipeConstraintSet(Array.Empty<int>(), Array.Empty<RecipeConditionInfo>());
		var itemByType = new Dictionary<int, ItemNode>();
		foreach ((int itemType, _) in ingredients)
			itemByType.TryAdd(itemType, new ItemNode(itemType));
		var target = new ItemNode(_nextItemType++);
		var recipe = new RecipeNode(_nextRecipeIndex--, target, outputStack, constraints);
		target.AddProducer(recipe);
		var edges = new List<IngredientEdge>();
		for (int ordinal = 0; ordinal < ingredients.Count; ordinal++) {
			(int itemType, int amount) = ingredients[ordinal];
			IReadOnlyList<RecipeGroupInfo> groups = groupIngredientOrdinal == ordinal
				? new[] { new RecipeGroupInfo(9000 + ordinal, itemType, new[] { itemType, itemType + 1000 }, "Synthetic Group") }
				: Array.Empty<RecipeGroupInfo>();
			var edge = new IngredientEdge(ordinal, itemByType[itemType], recipe, amount, groups);
			recipe.AddIngredient(edge);
			itemByType[itemType].AddConsumer(recipe);
			edges.Add(edge);
		}

		var recipes = new List<RecipeNode> { recipe };
		var productEdges = new List<ProductEdge> { recipe.Product };
		if (includeProducerForFirstIngredient) {
			ItemNode producedIngredient = itemByType[ingredients[0].ItemType];
			var source = new ItemNode(_nextItemType++);
			itemByType.Add(source.ItemType, source);
			var producer = new RecipeNode(_nextRecipeIndex--, producedIngredient, 5, constraints);
			producedIngredient.AddProducer(producer);
			var producerEdge = new IngredientEdge(0, source, producer, 1, Array.Empty<RecipeGroupInfo>());
			producer.AddIngredient(producerEdge);
			source.AddConsumer(producer);
			edges.Add(producerEdge);
			recipes.Add(producer);
			productEdges.Add(producer.Product);
		}

		ItemNode[] items = itemByType.Values.Append(target).ToArray();
		var depths = new Dictionary<GraphNodeKey, int>();
		foreach (ItemNode item in items)
			depths[GraphNodeKey.From(item)] = ReferenceEquals(item, target) ? 0 : 1;
		foreach (RecipeNode includedRecipe in recipes)
			depths[GraphNodeKey.From(includedRecipe)] = ReferenceEquals(includedRecipe, recipe) ? 0 : 1;
		var subgraph = new TargetCraftSubgraph(
			target,
			items,
			recipes,
			edges,
			productEdges,
			depths,
			includedComponentIds: new[] { 1 },
			TargetSubgraphOptions.Bounded(2, 100),
			SubgraphTruncationReason.None,
			skippedFrontierCount: 0,
			alternativeProducerRecipeCount: 0,
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
			edge => new IngredientResolution(
				edge,
				edge.RequiredAmount,
				context.GetAvailableCount(edge.Source.ItemType),
				context.GetAvailableCount(edge.Source.ItemType) >= edge.RequiredAmount,
				edge.AcceptsRecipeGroup ? IngredientResolutionMode.RecipeGroup : IngredientResolutionMode.DirectItem,
				context.GetResourceEvidence(edge.Source.ItemType)));
		var recipeResolutions = new Dictionary<RecipeNode, RecipeResolution>();
		foreach (RecipeNode includedRecipe in recipes) {
			RecipeLocalState state = ReferenceEquals(includedRecipe, recipe) ? localState : RecipeLocalState.NeedsIngredients;
			recipeResolutions[includedRecipe] = CreateRecipeResolution(includedRecipe, state, ingredientResolutions);
		}
		var resolution = new ResolutionSnapshot(
			subgraph,
			context,
			itemResolutions,
			ingredientResolutions,
			recipeResolutions,
			TimeSpan.Zero);
		return new Fixture(subgraph, resolution, recipe, VirtualResourceLedger.Create(context.Resources));
	}

	private static RecipeResolution CreateRecipeResolution(
		RecipeNode recipe,
		RecipeLocalState state,
		IReadOnlyDictionary<IngredientEdge, IngredientResolution> ingredientResolutions)
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
			recipe.Ingredients.Select(ingredient => ingredientResolutions[ingredient]).ToArray(),
			station,
			conditions,
			ComponentId: 1,
			IsCyclic: false);
	}

	private static PlayerCraftContextSnapshot SyntheticContext(params (int ItemType, int Count)[] items) =>
		PlayerCraftContextSnapshot.CreateSynthetic(items.Select(item => new KeyValuePair<int, int>(item.ItemType, item.Count)));

	private static CraftResourceSourceSnapshot Source(string sourceId, params (int ItemType, int Count)[] items) =>
		new(
			new CraftResourceSourceDescriptor(
				new CraftResourceSourceId(sourceId),
				CraftResourceSourceKind.Synthetic,
				sourceId,
				CraftResourceConsumptionCapability.EvidenceOnly),
			items.Select(item => new KeyValuePair<int, int>(item.ItemType, item.Count)));

	private static string PlanSignature(SingleRecipePlanResult result) => string.Join("|",
		result.Status,
		result.CraftRepetitions,
		result.ProducedQuantity,
		string.Join(",", result.IngredientResults.Select(item =>
			$"{item.Ingredient.Ordinal}:{item.Ingredient.Source.ItemType}:{item.Status}:{item.RequiredQuantity}:{item.ReservedQuantity}:{item.RemainingAfter}")),
		string.Join(",", result.Reservations.Select(reservation =>
			$"{reservation.ItemType}:{reservation.Quantity}:{reservation.RemainingAfter}")));

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"Planner foundation synthetic validation failed: {message}");
	}

	private sealed record Fixture(
		TargetCraftSubgraph Subgraph,
		ResolutionSnapshot Resolution,
		RecipeNode Recipe,
		VirtualResourceLedger Ledger);
}
