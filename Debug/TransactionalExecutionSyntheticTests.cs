using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

internal sealed record TransactionalExecutionSyntheticReport(
	int PassedTestCount,
	int FaultRollbackCaseCount,
	CraftTransactionResult SuccessExample,
	CraftTransactionResult SurplusExample,
	CraftTransactionResult RollbackExample,
	TimeSpan Elapsed);

internal static class TransactionalExecutionSyntheticTests
{
	public static TransactionalExecutionSyntheticReport Run()
	{
		var timer = Stopwatch.StartNew();
		CraftTransactionResult success = TestT1DirectSuccess();
		TestT2SplitSlotConsumption();
		TestT3MultiSourceGenericTransaction();
		TestT4EvidenceOnlyRejected();
		TestT5TransactionLocalIntermediate();
		CraftTransactionResult surplus = TestT6IntermediateSurplus();
		TestT7ExternalAndLocalIntermediate();
		TestT8SharedResourceNoDoubleConsume();
		TestT9RecipeGroupConcreteConsumption();
		TestT10ChangedResource();
		TestT11ChangedEnvironment();
		TestT12ChangedCondition();
		TestT13OutputFull();
		CraftTransactionResult rollback = TestT14FailureAfterFirstConsumption();
		TestT15FailureDuringLaterStep();
		TestT16FailureBeforeOutput();
		TestT17FailureDuringOutput();
		TestT18DoubleExecute();
		TestT19DeterministicActualResult();
		int faultCases = TestT20NoDuplicationAcrossFaults();
		TestT21NoLossAfterRollback();
		TestT22PlannerArtifactImmutable();
		timer.Stop();
		return new TransactionalExecutionSyntheticReport(22, faultCases, success, surplus, rollback, timer.Elapsed);
	}

	private static CraftTransactionResult TestT1DirectSuccess()
	{
		var source = Source("m52:t1", (6101, 5));
		Fixture fixture = DirectFixture(610, 6100, 6101, 5, source.Snapshot());
		var sink = new SyntheticOutputSink();
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink);
		Assert(result.Status == CraftTransactionStatus.Success && result.Committed &&
			source.Count(6101) == 0 && sink.Count(6100) == 1,
			"T1 direct execution did not consume X5 and deliver Target1.");
		return result;
	}

	private static void TestT2SplitSlotConsumption()
	{
		var source = Source("m52:t2", (6111, 2), (6111, 3));
		Fixture fixture = DirectFixture(611, 6110, 6111, 5, source.Snapshot());
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink());
		Assert(result.Committed && source.Slots.All(slot => slot.Quantity == 0) &&
			result.ActualConsumptions.Select(item => item.SourceLocalLocator).SequenceEqual(new[] { "slot:0", "slot:1" }),
			"T2 split-slot consumption did not consume both exact locators.");
	}

	private static void TestT3MultiSourceGenericTransaction()
	{
		var a = Source("m52:t3:a", (6121, 3));
		var b = Source("m52:t3:b", (6121, 2));
		Fixture fixture = DirectFixture(612, 6120, 6121, 5, a.Snapshot(), b.Snapshot());
		CraftTransactionResult result = Execute(Plan(fixture), new[] { a, b }, new SyntheticOutputSink());
		Assert(result.Committed && a.Count(6121) == 0 && b.Count(6121) == 0 &&
			result.ActualConsumptions.Select(item => item.SourceId).Distinct().Count() == 2,
			"T3 core did not transact across two generic SourceIds.");
	}

	private static void TestT4EvidenceOnlyRejected()
	{
		CraftResourceSourceSnapshot evidence = Snapshot(
			"m52:t4",
			CraftResourceConsumptionCapability.EvidenceOnly,
			(6131, 5));
		Fixture fixture = DirectFixture(613, 6130, 6131, 5, evidence);
		TargetCraftPlanResult logical = LogicalPlan(fixture);
		CraftExecutionPreflightResult preflight = CraftExecutionPlanner.CreatePreflight(new(logical, fixture.Resolution));
		Assert(preflight.Status == ExecutionPreflightStatus.UnsupportedSourceCapability && preflight.TransactionPlan is null,
			"T4 EvidenceOnly source entered a transaction plan.");
	}

	private static void TestT5TransactionLocalIntermediate()
	{
		var source = Source("m52:t5", (6142, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(614, 6140, 1, RecipeLocalState.NeedsIngredients, true,
			new Spec(6141, 1, false));
		builder.AddRecipeWithIndex(6141, 6141, 1, RecipeLocalState.NeedsIngredients, true,
			new Spec(6142, 1, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		var sink = new SyntheticOutputSink();
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink);
		Assert(result.Committed && sink.Count(6140) == 1 && sink.Count(6141) == 0 &&
			result.ActualConsumptions.All(item => item.ItemType == 6142),
			"T5 intermediate B leaked to external source or output.");
	}

	private static CraftTransactionResult TestT6IntermediateSurplus()
	{
		var source = Source("m52:t6", (6152, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(615, 6150, 1, RecipeLocalState.NeedsIngredients, true,
			new Spec(6151, 3, false));
		builder.AddRecipeWithIndex(6151, 6151, 4, RecipeLocalState.NeedsIngredients, true,
			new Spec(6152, 1, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		var sink = new SyntheticOutputSink();
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink);
		Assert(result.Committed && sink.Count(6150) == 1 && sink.Count(6151) == 1 &&
			result.SurplusOutputs.Single().Quantity == 1,
			"T6 did not deliver B1 intermediate surplus with the final target.");
		return result;
	}

	private static void TestT7ExternalAndLocalIntermediate()
	{
		var source = Source("m52:t7", (6161, 1), (6162, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(616, 6160, 1, RecipeLocalState.NeedsIngredients, true,
			new Spec(6161, 3, false));
		builder.AddRecipeWithIndex(6161, 6161, 2, RecipeLocalState.NeedsIngredients, true,
			new Spec(6162, 1, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink());
		Assert(result.Committed && result.ActualConsumptions.Sum(item => item.ItemType == 6161 ? item.Quantity : 0) == 1 &&
			result.ActualConsumptions.Sum(item => item.ItemType == 6162 ? item.Quantity : 0) == 1,
			"T7 did not combine external B1 with transaction-local B2.");
	}

	private static void TestT8SharedResourceNoDoubleConsume()
	{
		var source = Source("m52:t8", (6171, 10));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(617, 6170, 1, RecipeLocalState.ReadyNow, true,
			new Spec(6171, 6, false), new Spec(6171, 4, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink());
		Assert(result.Committed && source.Count(6171) == 0 && result.ActualConsumptions.Sum(item => item.Quantity) == 10,
			"T8 shared resource was not consumed exactly once per allocated unit.");
	}

	private static void TestT9RecipeGroupConcreteConsumption()
	{
		var wood = Source("m52:t9:a", (6181, 1));
		var boreal = Source("m52:t9:b", (16181, 2));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(618, 6180, 1, RecipeLocalState.ReadyNow, true,
			new Spec(6181, 3, true));
		Fixture fixture = builder.Build(root, Context(wood.Snapshot(), boreal.Snapshot()));
		CraftTransactionResult result = Execute(Plan(fixture), new[] { wood, boreal }, new SyntheticOutputSink());
		Assert(result.Committed && result.ActualConsumptions
			.GroupBy(item => item.ItemType).ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity))
			.OrderBy(pair => pair.Key).SequenceEqual(new[] {
				new KeyValuePair<int, long>(6181, 1), new KeyValuePair<int, long>(16181, 2) }),
			"T9 did not consume the Planner's concrete Recipe Group allocation.");
	}

	private static void TestT10ChangedResource() => AssertPreMutationFailure(
		ExecutionPreflightStatus.SourceQuantityChanged,
		CraftTransactionStatus.SourceChanged,
		"T10");

	private static void TestT11ChangedEnvironment() => AssertPreMutationFailure(
		ExecutionPreflightStatus.EnvironmentChanged,
		CraftTransactionStatus.EnvironmentChanged,
		"T11");

	private static void TestT12ChangedCondition() => AssertPreMutationFailure(
		ExecutionPreflightStatus.ConditionChanged,
		CraftTransactionStatus.ConditionChanged,
		"T12");

	private static void TestT13OutputFull()
	{
		var source = Source("m52:t13", (6221, 1));
		Fixture fixture = DirectFixture(622, 6220, 6221, 1, source.Snapshot());
		var sink = new SyntheticOutputSink(capacity: 0);
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink);
		Assert(result.Status == CraftTransactionStatus.OutputCapacityInsufficient && !result.MutationOccurred &&
			source.Count(6221) == 1 && sink.TotalCount == 0,
			"T13 full output sink mutated ingredients.");
	}

	private static CraftTransactionResult TestT14FailureAfterFirstConsumption()
	{
		var source = Source("m52:t14", (6231, 1), (6232, 1));
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(623, 6230, 1, RecipeLocalState.ReadyNow, true,
			new Spec(6231, 1, false), new Spec(6232, 1, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink(),
			new SyntheticFaultInjector(CraftTransactionFaultPoint.AfterSourceConsumption, 0));
		AssertRolledBack(result, source, new Dictionary<int, long> { [6231] = 1, [6232] = 1 }, "T14");
		return result;
	}

	private static void TestT15FailureDuringLaterStep()
	{
		var source = Source("m52:t15", (6242, 1));
		Fixture fixture = ChainFixture(624, 6240, 6241, 6242, source.Snapshot());
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink(),
			new SyntheticFaultInjector(CraftTransactionFaultPoint.BeforeCraftStep, 1));
		AssertRolledBack(result, source, new Dictionary<int, long> { [6242] = 1 }, "T15");
		Assert(result.DeliveredOutputs.Count == 0, "T15 leaked a final output.");
	}

	private static void TestT16FailureBeforeOutput()
	{
		var source = Source("m52:t16", (6251, 1));
		Fixture fixture = DirectFixture(625, 6250, 6251, 1, source.Snapshot());
		var sink = new SyntheticOutputSink();
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink,
			new SyntheticFaultInjector(CraftTransactionFaultPoint.BeforeOutputDelivery, 0));
		AssertRolledBack(result, source, new Dictionary<int, long> { [6251] = 1 }, "T16");
		Assert(sink.TotalCount == 0, "T16 delivered output before rollback.");
	}

	private static void TestT17FailureDuringOutput()
	{
		var source = Source("m52:t17", (6261, 1));
		Fixture fixture = DirectFixture(626, 6260, 6261, 1, source.Snapshot());
		var sink = new SyntheticOutputSink();
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink,
			new SyntheticFaultInjector(CraftTransactionFaultPoint.DuringOutputDelivery, 0));
		AssertRolledBack(result, source, new Dictionary<int, long> { [6261] = 1 }, "T17");
		Assert(sink.TotalCount == 0, "T17 partial output insertion was not rolled back.");
	}

	private static void TestT18DoubleExecute()
	{
		var source = Source("m52:t18", (6271, 1));
		Fixture fixture = DirectFixture(627, 6270, 6271, 1, source.Snapshot());
		var sink = new SyntheticOutputSink();
		CraftExecutionTransaction transaction = Transaction(Plan(fixture), new[] { source }, sink);
		CraftTransactionResult first = transaction.Execute();
		CraftTransactionResult second = transaction.Execute();
		Assert(first.Status == CraftTransactionStatus.Success && second.Status == CraftTransactionStatus.AlreadyCommitted &&
			second.RepeatedExecution && source.Count(6271) == 0 && sink.Count(6270) == 1,
			"T18 executed one committed transaction twice.");
	}

	private static void TestT19DeterministicActualResult()
	{
		string[] signatures = new string[2];
		for (int index = 0; index < 2; index++) {
			var source = Source($"m52:t19:{index}", (6281, 5));
			Fixture fixture = DirectFixture(628, 6280, 6281, 5, source.Snapshot());
			var semantics = new SyntheticRecipeSemantics((_, _, maximum) => maximum - 1);
			CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink(),
				semantics: semantics);
			signatures[index] = $"{result.Status}:{result.ActualConsumptions.Sum(item => item.Quantity)}:{source.Count(6281)}";
		}
		Assert(signatures[0] == signatures[1] && signatures[0].EndsWith(":4:1", StringComparison.Ordinal),
			"T19 deterministic actual-consumption result was not stable.");
	}

	private static int TestT20NoDuplicationAcrossFaults()
	{
		(CraftTransactionFaultPoint Point, int Ordinal)[] faults = {
			(CraftTransactionFaultPoint.AfterSourceConsumption, 0),
			(CraftTransactionFaultPoint.BeforeOutputDelivery, 0),
			(CraftTransactionFaultPoint.DuringOutputDelivery, 0),
			(CraftTransactionFaultPoint.AfterOutputDeliveryBeforeCommit, 0)
		};
		foreach ((CraftTransactionFaultPoint point, int ordinal) in faults) {
			var source = Source($"m52:t20:{point}", (6291, 1));
			Fixture fixture = DirectFixture(629, 6290, 6291, 1, source.Snapshot());
			var sink = new SyntheticOutputSink();
			CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, sink,
				new SyntheticFaultInjector(point, ordinal));
			Assert(result.RollbackSucceeded && source.Count(6291) == 1 && sink.Count(6290) == 0,
				$"T20 duplication invariant failed at {point}.");
		}
		return faults.Length;
	}

	private static void TestT21NoLossAfterRollback()
	{
		var source = Source("m52:t21", (6301, 2), (6302, 3));
		string before = source.Signature;
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(630, 6300, 1, RecipeLocalState.ReadyNow, true,
			new Spec(6301, 2, false), new Spec(6302, 3, false));
		Fixture fixture = builder.Build(root, Context(source.Snapshot()));
		CraftTransactionResult result = Execute(Plan(fixture), new[] { source }, new SyntheticOutputSink(),
			new SyntheticFaultInjector(CraftTransactionFaultPoint.AfterSourceConsumption, 1));
		Assert(result.RollbackSucceeded && source.Signature == before,
			"T21 rollback did not restore the exact synthetic slot state.");
	}

	private static void TestT22PlannerArtifactImmutable()
	{
		var source = Source("m52:t22", (6311, 1));
		Fixture fixture = DirectFixture(631, 6310, 6311, 1, source.Snapshot());
		ExecutableTransactionPlan plan = Plan(fixture);
		string before = PlanSignature(plan);
		CraftTransactionResult result = Execute(plan, new[] { source }, new SyntheticOutputSink());
		Assert(result.Committed && before == PlanSignature(plan) && !plan.MutatesRuntimeState,
			"T22 execution mutated the immutable Planner/execution-plan artifact.");
	}

	private static void AssertPreMutationFailure(
		ExecutionPreflightStatus validationStatus,
		CraftTransactionStatus expectedStatus,
		string test)
	{
		var source = Source($"m52:{test}", (6201, 1));
		Fixture fixture = DirectFixture(620, 6200, 6201, 1, source.Snapshot());
		CraftExecutionTransaction transaction = Transaction(
			Plan(fixture),
			new[] { source },
			new SyntheticOutputSink(),
			validation: new SyntheticFinalValidation(validationStatus));
		CraftTransactionResult result = transaction.Execute();
		Assert(result.Status == expectedStatus && !result.MutationOccurred && source.Count(6201) == 1,
			$"{test} final validation did not fail with zero mutation.");
	}

	private static void AssertRolledBack(
		CraftTransactionResult result,
		SyntheticTransactionAdapter source,
		IReadOnlyDictionary<int, long> expected,
		string test)
	{
		Assert(result.Status == CraftTransactionStatus.RollbackSucceededAfterFailure &&
			result.RollbackAttempted && result.RollbackSucceeded &&
			expected.All(pair => source.Count(pair.Key) == pair.Value),
			$"{test} did not restore every consumed resource.");
	}

	private static Fixture DirectFixture(
		int recipeIndex,
		int target,
		int ingredient,
		int required,
		params CraftResourceSourceSnapshot[] sources)
	{
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(recipeIndex, target, 1,
			RecipeLocalState.ReadyNow, true, new Spec(ingredient, required, false));
		return builder.Build(root, Context(sources));
	}

	private static Fixture ChainFixture(
		int rootRecipe,
		int target,
		int intermediate,
		int ingredient,
		params CraftResourceSourceSnapshot[] sources)
	{
		var builder = new Builder();
		RecipeNode root = builder.AddRecipeWithIndex(rootRecipe, target, 1,
			RecipeLocalState.NeedsIngredients, true, new Spec(intermediate, 1, false));
		builder.AddRecipeWithIndex(rootRecipe + 1, intermediate, 1,
			RecipeLocalState.NeedsIngredients, true, new Spec(ingredient, 1, false));
		return builder.Build(root, Context(sources));
	}

	private static TargetCraftPlanResult LogicalPlan(Fixture fixture) =>
		TargetCraftPlanner.Plan(new TargetCraftPlanningRequest(
			fixture.Subgraph,
			fixture.Resolution,
			fixture.Subgraph.Target,
			1,
			fixture.Ledger));

	private static ExecutableTransactionPlan Plan(Fixture fixture)
	{
		CraftExecutionPreflightResult preflight = CraftExecutionPlanner.CreatePreflight(new(
			LogicalPlan(fixture), fixture.Resolution));
		return preflight.TransactionPlan
			?? throw new InvalidOperationException($"M5.2 fixture preflight failed: {preflight.Status}.");
	}

	private static CraftTransactionResult Execute(
		ExecutableTransactionPlan plan,
		IReadOnlyList<SyntheticTransactionAdapter> sources,
		SyntheticOutputSink sink,
		ICraftTransactionFaultInjector? fault = null,
		SyntheticRecipeSemantics? semantics = null) =>
		Transaction(plan, sources, sink, fault, semantics).Execute();

	private static CraftExecutionTransaction Transaction(
		ExecutableTransactionPlan plan,
		IReadOnlyList<SyntheticTransactionAdapter> sources,
		SyntheticOutputSink sink,
		ICraftTransactionFaultInjector? fault = null,
		SyntheticRecipeSemantics? semantics = null,
		ICraftExecutionFinalValidationProvider? validation = null) =>
		new(
			plan,
			validation ?? new SyntheticFinalValidation(ExecutionPreflightStatus.Ready),
			new SyntheticAdapterRegistry(sources),
			semantics ?? new SyntheticRecipeSemantics(),
			sink,
			fault);

	private static PlayerCraftContextSnapshot Context(params CraftResourceSourceSnapshot[] sources) =>
		PlayerCraftContextSnapshot.CreateSynthetic(sources);

	private static SyntheticTransactionAdapter Source(
		string id,
		params (int ItemType, int Count)[] slots) => new(id, slots);

	private static CraftResourceSourceSnapshot Snapshot(
		string id,
		CraftResourceConsumptionCapability capability,
		params (int ItemType, int Count)[] resources) =>
		new(
			new CraftResourceSourceDescriptor(new CraftResourceSourceId(id), CraftResourceSourceKind.Synthetic,
				id, capability),
			resources.GroupBy(item => item.ItemType).Select(group =>
				new KeyValuePair<int, int>(group.Key, group.Sum(item => item.Count))));

	private static string PlanSignature(ExecutableTransactionPlan plan) => string.Join('|',
		plan.LogicalPlan.Status,
		string.Join(',', plan.Steps.Select(step => $"{step.Sequence}:{step.Recipe.RecipeIndex}:{step.Production.ItemType}:{step.Production.Quantity}")),
		string.Join(',', plan.Allocations.Select(allocation =>
			$"{allocation.SourceId}:{allocation.StepSequence}:{allocation.IngredientOrdinal}:{allocation.ItemType}:{allocation.Quantity}")));

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException($"M5.2 transactional execution synthetic validation failed: {message}");
	}

	private sealed record SyntheticSlot(int ItemType, long Quantity);

	private sealed class SyntheticPreparedState
	{
		public SyntheticPreparedState(IReadOnlyList<(int Slot, long Quantity)> segments)
		{
			Segments = segments;
		}

		public IReadOnlyList<(int Slot, long Quantity)> Segments { get; }
		public long Consumed { get; set; }
	}

	private sealed class SyntheticTransactionAdapter : ICraftResourceTransactionAdapter
	{
		private readonly List<SyntheticSlot> _slots;

		public SyntheticTransactionAdapter(string id, IEnumerable<(int ItemType, int Count)> slots)
		{
			Descriptor = new CraftResourceSourceDescriptor(new CraftResourceSourceId(id),
				CraftResourceSourceKind.Synthetic, id, CraftResourceConsumptionCapability.AdapterTransactionRequired);
			_slots = slots.Select(item => new SyntheticSlot(item.ItemType, item.Count)).ToList();
		}

		public CraftResourceSourceDescriptor Descriptor { get; }
		public IReadOnlyList<SyntheticSlot> Slots => _slots;
		public long TotalCount => _slots.Sum(slot => slot.Quantity);
		public long Count(int itemType) => _slots.Where(slot => slot.ItemType == itemType).Sum(slot => slot.Quantity);
		public string Signature => string.Join(',', _slots.Select((slot, index) => $"{index}:{slot.ItemType}:{slot.Quantity}"));

		public CraftResourceSourceSnapshot Snapshot() => new(
			Descriptor,
			_slots.GroupBy(slot => slot.ItemType).Select(group =>
				new KeyValuePair<int, int>(group.Key, checked((int)group.Sum(slot => slot.Quantity)))));

		public IReadOnlyList<CraftPreparedSourceAllocation> Prepare(IReadOnlyList<SourceResourceAllocation> allocations)
		{
			long[] remaining = _slots.Select(slot => slot.Quantity).ToArray();
			var result = new List<CraftPreparedSourceAllocation>();
			foreach (SourceResourceAllocation allocation in allocations) {
				long needed = allocation.Quantity;
				var segments = new List<(int Slot, long Quantity)>();
				for (int slot = 0; slot < _slots.Count && needed > 0; slot++) {
					if (_slots[slot].ItemType != allocation.ItemType)
						continue;
					long take = Math.Min(needed, remaining[slot]);
					if (take <= 0)
						continue;
					segments.Add((slot, take));
					remaining[slot] -= take;
					needed -= take;
				}
				if (needed != 0)
					throw new InvalidOperationException("Synthetic source became insufficient during prepare.");
				result.Add(new CraftPreparedSourceAllocation(allocation,
					new CraftSourceSlotLocator(string.Join(',', segments.Select(item => $"slot:{item.Slot}")),
						new SyntheticPreparedState(segments))));
			}
			return result;
		}

		public void ValidatePrepared(IReadOnlyList<CraftPreparedSourceAllocation> preparedAllocations)
		{
			foreach (CraftPreparedSourceAllocation prepared in preparedAllocations) {
				SyntheticPreparedState state = (SyntheticPreparedState)prepared.Locator.AdapterState;
				if (state.Segments.Any(segment => _slots[segment.Slot].ItemType != prepared.Allocation.ItemType ||
					_slots[segment.Slot].Quantity < segment.Quantity))
					throw new InvalidOperationException("Synthetic locator changed before mutation.");
			}
		}

		public IReadOnlyList<CraftConsumedResource> Consume(
			CraftPreparedSourceAllocation preparedAllocation,
			long quantity,
			int stepSequence,
			int ingredientOrdinal,
			CraftMutationJournal journal)
		{
			SyntheticPreparedState state = (SyntheticPreparedState)preparedAllocation.Locator.AdapterState;
			SyntheticSlot[] before = _slots.ToArray();
			journal.Record(new DelegateCraftMutationJournalEntry("Restore synthetic source", () => {
				_slots.Clear();
				_slots.AddRange(before);
			}));
			long remaining = quantity;
			long skipped = state.Consumed;
			var result = new List<CraftConsumedResource>();
			foreach ((int slot, long reserved) in state.Segments) {
				if (remaining == 0)
					break;
				if (skipped >= reserved) {
					skipped -= reserved;
					continue;
				}
				long take = Math.Min(remaining, reserved - skipped);
				skipped = 0;
				SyntheticSlot current = _slots[slot];
				if (current.ItemType != preparedAllocation.Allocation.ItemType || current.Quantity < take)
					throw new InvalidOperationException("Synthetic exact slot changed.");
				_slots[slot] = current with { Quantity = current.Quantity - take };
				var actual = new ActualResourceConsumption(Descriptor.SourceId, stepSequence, ingredientOrdinal,
					current.ItemType, take, $"slot:{slot}");
				result.Add(new CraftConsumedResource(actual,
					new CraftTransactionItemStack(current.ItemType, take, CraftTransactionOutputKind.ConsumedIngredient,
						stepSequence, -1)));
				remaining -= take;
			}
			if (remaining != 0)
				throw new InvalidOperationException("Synthetic prepared locator was insufficient.");
			state.Consumed += quantity;
			return result;
		}
	}

	private sealed class SyntheticAdapterRegistry : ICraftResourceTransactionAdapterRegistry
	{
		private readonly Dictionary<CraftResourceSourceId, ICraftResourceTransactionAdapter> _adapters;

		public SyntheticAdapterRegistry(IEnumerable<SyntheticTransactionAdapter> adapters) =>
			_adapters = adapters.ToDictionary(adapter => adapter.Descriptor.SourceId,
				adapter => (ICraftResourceTransactionAdapter)adapter);

		public bool TryGetAdapter(CraftResourceSourceId sourceId, out ICraftResourceTransactionAdapter? adapter) =>
			_adapters.TryGetValue(sourceId, out adapter);
	}

	private sealed class SyntheticRecipeSemantics : ICraftRecipeExecutionSemantics
	{
		private readonly Func<int, int, long, long> _actual;

		public SyntheticRecipeSemantics(Func<int, int, long, long>? actual = null) =>
			_actual = actual ?? ((_, _, maximum) => maximum);

		public CraftPreparedRecipeStep PrepareStep(ExecutionStepPlan step)
		{
			var crafts = new List<CraftPreparedRecipeCraft>();
			for (int repetition = 0; repetition < step.CraftRepetitions; repetition++) {
				var ingredients = step.LogicalStep.Ingredients.OrderBy(item => item.Ingredient.Ordinal)
					.Select(item => new CraftActualIngredientQuantity(
						item.Ingredient.Ordinal,
						_actual(step.Sequence, item.Ingredient.Ordinal, item.RequiredQuantity / step.CraftRepetitions)))
					.ToArray();
				var output = new CraftTransactionItemStack(step.Production.ItemType, step.Recipe.OutputStack,
					CraftTransactionOutputKind.StepProduction, step.Sequence, repetition);
				crafts.Add(new CraftPreparedRecipeCraft(repetition, ingredients, new[] { output }));
			}
			return new CraftPreparedRecipeStep(step, crafts);
		}

		public void CompleteCraft(ExecutionStepPlan step, CraftPreparedRecipeCraft craft,
			IReadOnlyList<CraftTransactionItemStack> consumedItems)
		{
		}

		public CraftTransactionItemStack Resize(CraftTransactionItemStack item, long quantity) =>
			item with { Quantity = quantity };
	}

	private sealed class SyntheticOutputSink : ICraftOutputSink
	{
		private readonly long _capacity;
		private readonly Dictionary<int, long> _items = new();

		public SyntheticOutputSink(long capacity = long.MaxValue) => _capacity = capacity;
		public long TotalCount => _items.Values.Sum();
		public long Count(int itemType) => _items.GetValueOrDefault(itemType);

		public bool CanAccept(CraftOutputCapacityRequest request, out string? issue)
		{
			bool accepted = checked(TotalCount + request.Outputs.Sum(item => item.Quantity)) <= _capacity;
			issue = accepted ? null : "Synthetic output sink is full.";
			return accepted;
		}

		public IReadOnlyList<CraftTransactionItemStack> Deliver(
			IReadOnlyList<CraftTransactionItemStack> outputs,
			CraftMutationJournal journal,
			Action<int> afterMutation)
		{
			Dictionary<int, long> before = new(_items);
			journal.Record(new DelegateCraftMutationJournalEntry("Restore synthetic output", () => {
				_items.Clear();
				foreach ((int type, long quantity) in before)
					_items[type] = quantity;
			}));
			int ordinal = 0;
			foreach (CraftTransactionItemStack output in outputs) {
				_items[output.ItemType] = checked(_items.GetValueOrDefault(output.ItemType) + output.Quantity);
				afterMutation(ordinal++);
			}
			return new ReadOnlyCollection<CraftTransactionItemStack>(outputs.ToArray());
		}
	}

	private sealed class SyntheticFinalValidation : ICraftExecutionFinalValidationProvider
	{
		private readonly ExecutionPreflightStatus _status;

		public SyntheticFinalValidation(ExecutionPreflightStatus status) => _status = status;

		public CraftExecutionFinalValidationResult Validate(ExecutableTransactionPlan plan) =>
			new(_status, _status == ExecutionPreflightStatus.Ready
				? Array.Empty<ExecutionPreflightIssue>()
				: new[] { new ExecutionPreflightIssue(ExecutionPreflightIssueKind.SourceQuantityDecreased,
					$"Synthetic final validation: {_status}.") });
	}

	private sealed class SyntheticFaultInjector : ICraftTransactionFaultInjector
	{
		private readonly CraftTransactionFaultPoint _point;
		private readonly int _ordinal;

		public SyntheticFaultInjector(CraftTransactionFaultPoint point, int ordinal)
		{
			_point = point;
			_ordinal = ordinal;
		}

		public void ThrowIfRequested(CraftTransactionFaultPoint point, int ordinal)
		{
			if (point == _point && ordinal == _ordinal)
				throw new InvalidOperationException($"Synthetic fault at {point}[{ordinal}].");
		}
	}
}
