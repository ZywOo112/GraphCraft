using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Execution;

public sealed class CraftExecutionTransaction
{
	private readonly object _gate = new();
	private readonly ExecutableTransactionPlan _plan;
	private readonly ICraftExecutionFinalValidationProvider _finalValidation;
	private readonly ICraftResourceTransactionAdapterRegistry _adapterRegistry;
	private readonly ICraftRecipeExecutionSemantics _recipeSemantics;
	private readonly ICraftOutputSink _outputSink;
	private readonly ICraftTransactionFaultInjector _faultInjector;
	private CraftTransactionResult? _terminalResult;

	public CraftExecutionTransaction(
		ExecutableTransactionPlan plan,
		ICraftExecutionFinalValidationProvider finalValidation,
		ICraftResourceTransactionAdapterRegistry adapterRegistry,
		ICraftRecipeExecutionSemantics recipeSemantics,
		ICraftOutputSink outputSink,
		ICraftTransactionFaultInjector? faultInjector = null)
	{
		_plan = plan ?? throw new ArgumentNullException(nameof(plan));
		_finalValidation = finalValidation ?? throw new ArgumentNullException(nameof(finalValidation));
		_adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
		_recipeSemantics = recipeSemantics ?? throw new ArgumentNullException(nameof(recipeSemantics));
		_outputSink = outputSink ?? throw new ArgumentNullException(nameof(outputSink));
		_faultInjector = faultInjector ?? NoCraftTransactionFaultInjector.Instance;
		TransactionId = Guid.NewGuid();
	}

	public Guid TransactionId { get; }

	public CraftTransactionState State { get; private set; } = CraftTransactionState.Created;

	public CraftTransactionResult Execute()
	{
		lock (_gate) {
			if (_terminalResult is not null)
				return RepeatResult(_terminalResult);

			var actualConsumptions = new List<ActualResourceConsumption>();
			var producedOutputs = new List<CraftTransactionItemStack>();
			var deliveredOutputs = new List<CraftTransactionItemStack>();
			var surplusOutputs = new List<CraftTransactionItemStack>();
			var issues = new List<CraftTransactionIssue>();
			var journal = new CraftMutationJournal();
			bool mutationOccurred = false;

			try {
				State = CraftTransactionState.Validating;
				CraftExecutionFinalValidationResult validation = _finalValidation.Validate(_plan);
				if (!validation.Succeeded) {
					CraftTransactionStatus status = StatusFor(validation.Status);
					issues.AddRange(validation.Issues.Select(issue => new CraftTransactionIssue(status, issue.Message,
						issue.StepSequence, issue.RecipeIndex, issue.SourceId)));
					State = CraftTransactionState.FailedBeforeMutation;
					return Finish(status, mutationOccurred, false, false, false, actualConsumptions,
						producedOutputs, deliveredOutputs, surplusOutputs, issues);
				}

				IReadOnlyList<CraftPreparedSourceAllocation> preparedSources = PrepareSources();
				foreach (IGrouping<ICraftResourceTransactionAdapter, CraftPreparedSourceAllocation> group in preparedSources
					.GroupBy(prepared => ResolveAdapter(prepared.Allocation.SourceId))) {
					try {
						group.Key.ValidatePrepared(group.ToArray());
					}
					catch (Exception exception)
					{
						throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
							$"Source {group.Key.Descriptor.SourceId} locators changed before mutation.", exception);
					}
				}

				PreparedTransaction blueprint = PrepareTransaction(preparedSources);
				if (!_outputSink.CanAccept(
					new CraftOutputCapacityRequest(blueprint.DeliveryPreview, blueprint.ExternalConsumptions),
					out string? capacityIssue)) {
					throw new CraftTransactionException(
						CraftTransactionStatus.OutputCapacityInsufficient,
						capacityIssue ?? "The output sink cannot accept every final and surplus output.");
				}

				State = CraftTransactionState.Validated;
				State = CraftTransactionState.Executing;
				var localItems = new List<CraftTransactionItemStack>();
				var finalItems = new List<CraftTransactionItemStack>();
				int sourceMutationOrdinal = 0;

				foreach (PreparedStepExecution stepExecution in blueprint.Steps) {
					_faultInjector.ThrowIfRequested(CraftTransactionFaultPoint.BeforeCraftStep, stepExecution.Step.Sequence);
					foreach (PreparedCraftExecution craftExecution in stepExecution.Crafts) {
						var consumedForHook = new List<CraftTransactionItemStack>();
						foreach (PreparedConcreteConsumption consumption in craftExecution.Consumptions) {
							consumedForHook.AddRange(TakeLocalItems(
								localItems,
								consumption.ItemType,
								consumption.LocalQuantity));

							foreach (CraftPreparedExternalConsumption external in consumption.ExternalConsumptions) {
								ICraftResourceTransactionAdapter adapter = ResolveAdapter(
									external.PreparedAllocation.Allocation.SourceId);
								IReadOnlyList<CraftConsumedResource> consumed;
								try {
									consumed = adapter.Consume(
										external.PreparedAllocation,
										external.Quantity,
										external.StepSequence,
										external.IngredientOrdinal,
										journal);
								}
								catch (Exception exception)
								{
									throw new CraftTransactionException(
										CraftTransactionStatus.ConsumptionFailed,
										$"Source {external.PreparedAllocation.Allocation.SourceId} failed during consumption.",
										exception);
								}

								long consumedQuantity = consumed.Sum(item => item.Consumption.Quantity);
								if (consumedQuantity != external.Quantity)
									throw new CraftTransactionException(CraftTransactionStatus.ConsumptionFailed,
										$"Source adapter consumed x{consumedQuantity}; exactly x{external.Quantity} was requested.");
								actualConsumptions.AddRange(consumed.Select(item => item.Consumption));
								consumedForHook.AddRange(consumed.Select(item => item.Item));
								mutationOccurred = mutationOccurred || journal.Count > 0;
								_faultInjector.ThrowIfRequested(CraftTransactionFaultPoint.AfterSourceConsumption, sourceMutationOrdinal++);
							}
						}

						try {
							_recipeSemantics.CompleteCraft(stepExecution.Step, craftExecution.Craft, consumedForHook);
						}
						catch (Exception exception)
						{
							throw new CraftTransactionException(CraftTransactionStatus.CraftStepFailed,
								$"Recipe #{stepExecution.Step.Recipe.RecipeIndex} OnCraft semantics failed.", exception);
						}

						ValidateCraftOutputs(stepExecution.Step, craftExecution.Craft);
						foreach (CraftTransactionItemStack output in craftExecution.Craft.Outputs) {
							producedOutputs.Add(output with { Kind = CraftTransactionOutputKind.StepProduction });
							if (stepExecution.Step.Production.Kind == ExecutionProductionKind.FinalOutput)
								finalItems.Add(output with { Kind = CraftTransactionOutputKind.FinalOutput });
							else
								localItems.Add(output with { Kind = CraftTransactionOutputKind.StepProduction });
						}
					}
				}

				surplusOutputs.AddRange(localItems.Select(item =>
					item with { Kind = CraftTransactionOutputKind.IntermediateSurplus }));
				CraftTransactionItemStack[] delivery = finalItems.Concat(surplusOutputs).ToArray();
				if (!_outputSink.CanAccept(
					new CraftOutputCapacityRequest(delivery, Array.Empty<CraftPreparedExternalConsumption>()),
					out string? liveCapacityIssue)) {
					throw new CraftTransactionException(CraftTransactionStatus.OutputCapacityInsufficient,
						liveCapacityIssue ?? "The output sink changed after source consumption.");
				}

				_faultInjector.ThrowIfRequested(CraftTransactionFaultPoint.BeforeOutputDelivery, 0);
				State = CraftTransactionState.DeliveringOutputs;
				try {
					deliveredOutputs.AddRange(_outputSink.Deliver(
						delivery,
						journal,
						ordinal => _faultInjector.ThrowIfRequested(CraftTransactionFaultPoint.DuringOutputDelivery, ordinal)));
				}
				catch (Exception exception)
				{
					throw new CraftTransactionException(CraftTransactionStatus.OutputDeliveryFailed,
						"The output sink failed while delivering final or surplus outputs.", exception);
				}

				mutationOccurred = mutationOccurred || journal.Count > 0;
				_faultInjector.ThrowIfRequested(CraftTransactionFaultPoint.AfterOutputDeliveryBeforeCommit, 0);
				State = CraftTransactionState.Committing;
				journal.Commit();
				State = CraftTransactionState.Committed;
				return Finish(CraftTransactionStatus.Success, mutationOccurred, true, false, false,
					actualConsumptions, producedOutputs, deliveredOutputs, surplusOutputs, issues);
			}
			catch (Exception exception)
			{
				mutationOccurred = mutationOccurred || journal.Count > 0;
				CraftTransactionStatus cause = exception is CraftTransactionException transactionException
					? transactionException.Status
					: State == CraftTransactionState.DeliveringOutputs
						? CraftTransactionStatus.OutputDeliveryFailed
						: CraftTransactionStatus.CraftStepFailed;
				issues.Add(new CraftTransactionIssue(cause, exception.Message, Exception: exception));
				if (!mutationOccurred) {
					State = CraftTransactionState.FailedBeforeMutation;
					return Finish(cause, false, false, false, false, actualConsumptions,
						producedOutputs, deliveredOutputs, surplusOutputs, issues);
				}

				State = CraftTransactionState.FailedAfterMutation;
				State = CraftTransactionState.RollingBack;
				IReadOnlyList<Exception> rollbackFailures = journal.Rollback();
				if (rollbackFailures.Count == 0) {
					State = CraftTransactionState.RolledBack;
					return Finish(CraftTransactionStatus.RollbackSucceededAfterFailure, true, false, true, true,
						actualConsumptions, producedOutputs, Array.Empty<CraftTransactionItemStack>(), surplusOutputs, issues);
				}

				State = CraftTransactionState.RollbackFailed;
				issues.AddRange(rollbackFailures.Select(failure => new CraftTransactionIssue(
					CraftTransactionStatus.RollbackFailed,
					"CRITICAL: transaction rollback failed and runtime state may be inconsistent.",
					Exception: failure)));
				return Finish(CraftTransactionStatus.RollbackFailed, true, false, true, false,
					actualConsumptions, producedOutputs, deliveredOutputs, surplusOutputs, issues);
			}
		}
	}

	private IReadOnlyList<CraftPreparedSourceAllocation> PrepareSources()
	{
		var prepared = new List<CraftPreparedSourceAllocation>();
		foreach (IGrouping<CraftResourceSourceId, SourceResourceAllocation> group in _plan.Allocations
			.GroupBy(allocation => allocation.SourceId)
			.OrderBy(group => group.Key.Value, StringComparer.Ordinal)) {
			ICraftResourceTransactionAdapter adapter = ResolveAdapter(group.Key);
			SourceResourceAllocation first = group.First();
			if (first.ConsumptionCapability != CraftResourceConsumptionCapability.AdapterTransactionRequired ||
				adapter.Descriptor.SourceId != first.SourceId ||
				adapter.Descriptor.SourceKind != first.SourceKind ||
				adapter.Descriptor.ConsumptionCapability != CraftResourceConsumptionCapability.AdapterTransactionRequired) {
				throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
					$"Source adapter descriptor no longer matches allocation {first.SourceId}.");
			}

			SourceResourceAllocation[] allocations = group
				.OrderBy(allocation => allocation.StepSequence)
				.ThenBy(allocation => allocation.IngredientOrdinal)
				.ThenBy(allocation => allocation.ItemType)
				.ToArray();
			IReadOnlyList<CraftPreparedSourceAllocation> sourcePrepared;
			try {
				sourcePrepared = adapter.Prepare(allocations);
			}
			catch (Exception exception)
			{
				throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
					$"Source adapter {first.SourceId} could not resolve its exact source-local locators.", exception);
			}
			if (sourcePrepared.Count != allocations.Length ||
				!sourcePrepared.Select(item => item.Allocation).SequenceEqual(allocations)) {
				throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
					$"Source adapter {first.SourceId} did not preserve every exact M5.1 allocation.");
			}
			prepared.AddRange(sourcePrepared);
		}
		return new ReadOnlyCollection<CraftPreparedSourceAllocation>(prepared);
	}

	private PreparedTransaction PrepareTransaction(IReadOnlyList<CraftPreparedSourceAllocation> preparedSources)
	{
		var handles = preparedSources.ToDictionary(prepared => prepared.Allocation);
		var sourceCursors = _plan.Allocations.ToDictionary(
			allocation => allocation,
			allocation => new SourceCursor(handles[allocation], allocation.Quantity));
		var simulatedLocal = new List<CraftTransactionItemStack>();
		var finalOutputs = new List<CraftTransactionItemStack>();
		var preparedSteps = new List<PreparedStepExecution>();
		var allExternal = new List<CraftPreparedExternalConsumption>();

		foreach (ExecutionStepPlan step in _plan.Steps.OrderBy(item => item.Sequence)) {
			CraftPreparedRecipeStep prepared;
			try {
				prepared = _recipeSemantics.PrepareStep(step);
			}
			catch (NotSupportedException exception)
			{
				throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
					$"Recipe #{step.Recipe.RecipeIndex} uses unsupported execution semantics.", exception);
			}
			catch (Exception exception)
			{
				throw new CraftTransactionException(CraftTransactionStatus.CraftStepFailed,
					$"Recipe #{step.Recipe.RecipeIndex} failed while preparing one-time consumption/output semantics.", exception);
			}
			if (!ReferenceEquals(prepared.Step, step) || prepared.Crafts.Count != step.CraftRepetitions)
				throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
					$"Recipe #{step.Recipe.RecipeIndex} did not prepare exactly {step.CraftRepetitions} craft repetitions.");
			ValidatePreparedStep(step, prepared);

			var demandRemaining = step.Demands.ToDictionary(demand => demand, demand => demand.RequiredQuantity);
			var localRemaining = step.Demands.ToDictionary(demand => demand, demand => demand.TransactionLocalQuantity);
			var externalRemaining = step.Demands.ToDictionary(demand => demand, demand => demand.ExternalSourceQuantity);
			var crafts = new List<PreparedCraftExecution>();
			foreach (CraftPreparedRecipeCraft craft in prepared.Crafts.OrderBy(item => item.Repetition)) {
				var concreteConsumptions = new List<PreparedConcreteConsumption>();
				foreach (CraftActualIngredientQuantity actual in craft.ActualIngredients.OrderBy(item => item.IngredientOrdinal)) {
					long unassigned = actual.Quantity;
					ExecutionResourceDemand[] candidates = step.Demands
						.Where(demand => demand.IngredientOrdinal == actual.IngredientOrdinal)
						.OrderBy(demand => demand.ItemType)
						.ToArray();
					foreach (ExecutionResourceDemand demand in candidates) {
						long quantity = Math.Min(unassigned, demandRemaining[demand]);
						if (quantity <= 0)
							continue;
						long local = Math.Min(quantity, Math.Min(localRemaining[demand], Count(simulatedLocal, demand.ItemType)));
						long external = quantity - local;
						if (external > externalRemaining[demand])
							throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
								$"Actual Recipe semantics exceeded the reserved external maximum for ItemType {demand.ItemType}.");

						TakeLocalItems(simulatedLocal, demand.ItemType, local);
						var externalParts = AllocatePreparedSources(sourceCursors, demand, external);
						allExternal.AddRange(externalParts);
						concreteConsumptions.Add(new PreparedConcreteConsumption(
							demand.ItemType, local, externalParts));
						demandRemaining[demand] -= quantity;
						localRemaining[demand] -= local;
						externalRemaining[demand] -= external;
						unassigned -= quantity;
					}
					if (unassigned != 0)
						throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
							$"Actual consumption x{actual.Quantity} exceeded the M5.1 maximum for ingredient #{actual.IngredientOrdinal}.");
				}

				crafts.Add(new PreparedCraftExecution(craft, concreteConsumptions));
				if (step.Production.Kind == ExecutionProductionKind.FinalOutput)
					finalOutputs.AddRange(craft.Outputs.Select(output => output with { Kind = CraftTransactionOutputKind.FinalOutput }));
				else
					simulatedLocal.AddRange(craft.Outputs);
			}
			preparedSteps.Add(new PreparedStepExecution(step, crafts));
		}

		CraftTransactionItemStack[] surplus = simulatedLocal
			.Select(item => item with { Kind = CraftTransactionOutputKind.IntermediateSurplus })
			.ToArray();
		CraftTransactionItemStack[] delivery = finalOutputs.Concat(surplus).ToArray();
		return new PreparedTransaction(preparedSteps, allExternal, delivery);
	}

	private IReadOnlyList<CraftPreparedExternalConsumption> AllocatePreparedSources(
		IReadOnlyDictionary<SourceResourceAllocation, SourceCursor> cursors,
		ExecutionResourceDemand demand,
		long quantity)
	{
		var result = new List<CraftPreparedExternalConsumption>();
		long remaining = quantity;
		foreach (SourceCursor cursor in cursors
			.Where(pair => pair.Key.StepSequence == demand.StepSequence &&
				pair.Key.IngredientOrdinal == demand.IngredientOrdinal &&
				pair.Key.ItemType == demand.ItemType)
			.OrderBy(pair => pair.Key.SourceId.Value, StringComparer.Ordinal)
			.Select(pair => pair.Value)) {
			long take = Math.Min(remaining, cursor.Remaining);
			if (take <= 0)
				continue;
			result.Add(new CraftPreparedExternalConsumption(
				cursor.Prepared,
				take,
				demand.StepSequence,
				demand.IngredientOrdinal,
				demand.ItemType));
			cursor.Remaining -= take;
			remaining -= take;
			if (remaining == 0)
				break;
		}
		if (remaining != 0)
			throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
				$"Prepared source allocations are short x{remaining} for ItemType {demand.ItemType}.");
		return result;
	}

	private static void ValidatePreparedStep(ExecutionStepPlan step, CraftPreparedRecipeStep prepared)
	{
		long outputQuantity = prepared.Crafts.SelectMany(craft => craft.Outputs)
			.Where(output => output.ItemType == step.Production.ItemType)
			.Sum(output => output.Quantity);
		if (prepared.Crafts.SelectMany(craft => craft.Outputs).Any(output =>
			output.ItemType != step.Production.ItemType || output.Quantity <= 0) ||
			outputQuantity != step.Production.Quantity) {
			throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
				$"Recipe #{step.Recipe.RecipeIndex} output differs from the immutable execution plan.");
		}

		foreach (IGrouping<int, CraftActualIngredientQuantity> group in prepared.Crafts
			.SelectMany(craft => craft.ActualIngredients)
			.GroupBy(item => item.IngredientOrdinal)) {
			long maximum = step.Demands.Where(demand => demand.IngredientOrdinal == group.Key)
				.Sum(demand => demand.RequiredQuantity);
			long actual = group.Sum(item => item.Quantity);
			if (actual < 0 || actual > maximum)
				throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
					$"Recipe #{step.Recipe.RecipeIndex} actual ingredient #{group.Key} x{actual} is outside 0..{maximum}.");
		}
	}

	private static void ValidateCraftOutputs(ExecutionStepPlan step, CraftPreparedRecipeCraft craft)
	{
		if (craft.Outputs.Any(output => output.ItemType != step.Production.ItemType || output.Quantity <= 0))
			throw new CraftTransactionException(CraftTransactionStatus.UnsupportedExecutionSemantics,
				$"Recipe #{step.Recipe.RecipeIndex} hooks changed output identity or quantity beyond the current transaction boundary.");
	}

	private IReadOnlyList<CraftTransactionItemStack> TakeLocalItems(
		List<CraftTransactionItemStack> items,
		int itemType,
		long quantity)
	{
		if (quantity == 0)
			return Array.Empty<CraftTransactionItemStack>();
		var consumed = new List<CraftTransactionItemStack>();
		long remaining = quantity;
		for (int index = 0; index < items.Count && remaining > 0;) {
			CraftTransactionItemStack item = items[index];
			if (item.ItemType != itemType) {
				index++;
				continue;
			}
			long take = Math.Min(remaining, item.Quantity);
			consumed.Add(_recipeSemantics.Resize(item, take));
			if (take == item.Quantity)
				items.RemoveAt(index);
			else {
				items[index] = _recipeSemantics.Resize(item, item.Quantity - take);
				index++;
			}
			remaining -= take;
		}
		if (remaining != 0)
			throw new CraftTransactionException(CraftTransactionStatus.CraftStepFailed,
				$"Transaction-local ItemType {itemType} was short x{remaining}; no external fallback is allowed.");
		return consumed;
	}

	private static long Count(IEnumerable<CraftTransactionItemStack> items, int itemType) =>
		items.Where(item => item.ItemType == itemType).Sum(item => item.Quantity);

	private ICraftResourceTransactionAdapter ResolveAdapter(CraftResourceSourceId sourceId)
	{
		if (!_adapterRegistry.TryGetAdapter(sourceId, out ICraftResourceTransactionAdapter? adapter) || adapter is null)
			throw new CraftTransactionException(CraftTransactionStatus.SourceChanged,
				$"No transaction adapter is registered for SourceId {sourceId}.");
		return adapter;
	}

	private CraftTransactionResult Finish(
		CraftTransactionStatus status,
		bool mutationOccurred,
		bool committed,
		bool rollbackAttempted,
		bool rollbackSucceeded,
		IReadOnlyList<ActualResourceConsumption> actualConsumptions,
		IReadOnlyList<CraftTransactionItemStack> producedOutputs,
		IReadOnlyList<CraftTransactionItemStack> deliveredOutputs,
		IReadOnlyList<CraftTransactionItemStack> surplusOutputs,
		IReadOnlyList<CraftTransactionIssue> issues)
	{
		_terminalResult = new CraftTransactionResult(
			TransactionId, status, State, mutationOccurred, committed, rollbackAttempted, rollbackSucceeded,
			repeatedExecution: false, actualConsumptions, producedOutputs, deliveredOutputs, surplusOutputs, issues);
		return _terminalResult;
	}

	private static CraftTransactionResult RepeatResult(CraftTransactionResult result) =>
		new(
			result.TransactionId,
			result.Committed ? CraftTransactionStatus.AlreadyCommitted : result.Status,
			result.State,
			result.MutationOccurred,
			result.Committed,
			result.RollbackAttempted,
			result.RollbackSucceeded,
			repeatedExecution: true,
			result.ActualConsumptions,
			result.ProducedOutputs,
			result.DeliveredOutputs,
			result.SurplusOutputs,
			result.Issues);

	private static CraftTransactionStatus StatusFor(ExecutionPreflightStatus status) => status switch
	{
		ExecutionPreflightStatus.SourceUnavailable or
		ExecutionPreflightStatus.SourceDescriptorChanged or
		ExecutionPreflightStatus.SourceQuantityChanged => CraftTransactionStatus.SourceChanged,
		ExecutionPreflightStatus.EnvironmentChanged => CraftTransactionStatus.EnvironmentChanged,
		ExecutionPreflightStatus.ConditionChanged => CraftTransactionStatus.ConditionChanged,
		_ => CraftTransactionStatus.PreMutationValidationFailed
	};

	private sealed class SourceCursor
	{
		public SourceCursor(CraftPreparedSourceAllocation prepared, long remaining)
		{
			Prepared = prepared;
			Remaining = remaining;
		}

		public CraftPreparedSourceAllocation Prepared { get; }
		public long Remaining { get; set; }
	}

	private sealed record PreparedConcreteConsumption(
		int ItemType,
		long LocalQuantity,
		IReadOnlyList<CraftPreparedExternalConsumption> ExternalConsumptions);

	private sealed record PreparedCraftExecution(
		CraftPreparedRecipeCraft Craft,
		IReadOnlyList<PreparedConcreteConsumption> Consumptions);

	private sealed record PreparedStepExecution(
		ExecutionStepPlan Step,
		IReadOnlyList<PreparedCraftExecution> Crafts);

	private sealed record PreparedTransaction(
		IReadOnlyList<PreparedStepExecution> Steps,
		IReadOnlyList<CraftPreparedExternalConsumption> ExternalConsumptions,
		IReadOnlyList<CraftTransactionItemStack> DeliveryPreview);

	private sealed class CraftTransactionException : Exception
	{
		public CraftTransactionException(CraftTransactionStatus status, string message, Exception? innerException = null)
			: base(message, innerException)
		{
			Status = status;
		}

		public CraftTransactionStatus Status { get; }
	}
}
