using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Resources;

namespace GraphCraft.Core.Execution;

public enum CraftTransactionState
{
	Created,
	Validating,
	Validated,
	Executing,
	DeliveringOutputs,
	Committing,
	Committed,
	RollingBack,
	RolledBack,
	FailedBeforeMutation,
	FailedAfterMutation,
	RollbackFailed
}

public enum CraftTransactionStatus
{
	Success,
	AlreadyCommitted,
	PreMutationValidationFailed,
	SourceChanged,
	EnvironmentChanged,
	ConditionChanged,
	OutputCapacityInsufficient,
	ConsumptionFailed,
	CraftStepFailed,
	OutputDeliveryFailed,
	RollbackSucceededAfterFailure,
	RollbackFailed,
	UnsupportedExecutionSemantics,
	UnsupportedMultiplayerContext
}

public enum CraftTransactionOutputKind
{
	ConsumedIngredient,
	StepProduction,
	FinalOutput,
	IntermediateSurplus
}

public enum CraftTransactionFaultPoint
{
	AfterSourceConsumption,
	BeforeCraftStep,
	BeforeOutputDelivery,
	DuringOutputDelivery,
	AfterOutputDeliveryBeforeCommit
}

public sealed record CraftTransactionIssue(
	CraftTransactionStatus Status,
	string Message,
	int? StepSequence = null,
	int? RecipeIndex = null,
	CraftResourceSourceId? SourceId = null,
	string? SourceLocalLocator = null,
	Exception? Exception = null);

public sealed record ActualResourceConsumption(
	CraftResourceSourceId SourceId,
	int StepSequence,
	int IngredientOrdinal,
	int ItemType,
	long Quantity,
	string SourceLocalLocator);

public sealed record CraftTransactionItemStack(
	int ItemType,
	long Quantity,
	CraftTransactionOutputKind Kind,
	int OriginStepSequence,
	int CraftRepetition,
	object? RuntimePayload = null)
{
	public CraftTransactionItemStack With(
		long quantity,
		CraftTransactionOutputKind? kind = null,
		object? runtimePayload = null) =>
		new(ItemType, quantity, kind ?? Kind, OriginStepSequence, CraftRepetition, runtimePayload ?? RuntimePayload);
}

public sealed record CraftConsumedResource(
	ActualResourceConsumption Consumption,
	CraftTransactionItemStack Item);

public sealed class CraftTransactionResult
{
	internal CraftTransactionResult(
		Guid transactionId,
		CraftTransactionStatus status,
		CraftTransactionState state,
		bool mutationOccurred,
		bool committed,
		bool rollbackAttempted,
		bool rollbackSucceeded,
		bool repeatedExecution,
		IReadOnlyList<ActualResourceConsumption> actualConsumptions,
		IReadOnlyList<CraftTransactionItemStack> producedOutputs,
		IReadOnlyList<CraftTransactionItemStack> deliveredOutputs,
		IReadOnlyList<CraftTransactionItemStack> surplusOutputs,
		IReadOnlyList<CraftTransactionIssue> issues)
	{
		TransactionId = transactionId;
		Status = status;
		State = state;
		MutationOccurred = mutationOccurred;
		Committed = committed;
		RollbackAttempted = rollbackAttempted;
		RollbackSucceeded = rollbackSucceeded;
		RepeatedExecution = repeatedExecution;
		ActualConsumptions = Copy(actualConsumptions);
		ProducedOutputs = Copy(producedOutputs);
		DeliveredOutputs = Copy(deliveredOutputs);
		SurplusOutputs = Copy(surplusOutputs);
		Issues = new ReadOnlyCollection<CraftTransactionIssue>(issues.ToArray());
	}

	public Guid TransactionId { get; }
	public CraftTransactionStatus Status { get; }
	public CraftTransactionState State { get; }
	public bool MutationOccurred { get; }
	public bool Committed { get; }
	public bool RollbackAttempted { get; }
	public bool RollbackSucceeded { get; }
	public bool RepeatedExecution { get; }
	public IReadOnlyList<ActualResourceConsumption> ActualConsumptions { get; }
	public IReadOnlyList<CraftTransactionItemStack> ProducedOutputs { get; }
	public IReadOnlyList<CraftTransactionItemStack> DeliveredOutputs { get; }
	public IReadOnlyList<CraftTransactionItemStack> SurplusOutputs { get; }
	public IReadOnlyList<CraftTransactionIssue> Issues { get; }

	private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source) =>
		new ReadOnlyCollection<T>(source.ToArray());
}

public sealed record CraftExecutionFinalValidationResult(
	ExecutionPreflightStatus Status,
	IReadOnlyList<ExecutionPreflightIssue> Issues)
{
	public bool Succeeded => Status == ExecutionPreflightStatus.Ready;
}

public interface ICraftExecutionFinalValidationProvider
{
	CraftExecutionFinalValidationResult Validate(ExecutableTransactionPlan plan);
}

public sealed record CraftSourceSlotLocator(
	string Display,
	object AdapterState);

public sealed record CraftPreparedSourceAllocation(
	SourceResourceAllocation Allocation,
	CraftSourceSlotLocator Locator);

public interface ICraftResourceTransactionAdapter
{
	CraftResourceSourceDescriptor Descriptor { get; }

	IReadOnlyList<CraftPreparedSourceAllocation> Prepare(
		IReadOnlyList<SourceResourceAllocation> allocations);

	void ValidatePrepared(IReadOnlyList<CraftPreparedSourceAllocation> preparedAllocations);

	IReadOnlyList<CraftConsumedResource> Consume(
		CraftPreparedSourceAllocation preparedAllocation,
		long quantity,
		int stepSequence,
		int ingredientOrdinal,
		CraftMutationJournal journal);
}

public interface ICraftResourceTransactionAdapterRegistry
{
	bool TryGetAdapter(CraftResourceSourceId sourceId, out ICraftResourceTransactionAdapter? adapter);
}

public sealed record CraftActualIngredientQuantity(int IngredientOrdinal, long Quantity);

public sealed class CraftPreparedRecipeCraft
{
	public CraftPreparedRecipeCraft(
		int repetition,
		IReadOnlyList<CraftActualIngredientQuantity> actualIngredients,
		IReadOnlyList<CraftTransactionItemStack> outputs,
		object? semanticsState = null)
	{
		Repetition = repetition;
		ActualIngredients = new ReadOnlyCollection<CraftActualIngredientQuantity>(actualIngredients.ToArray());
		Outputs = new ReadOnlyCollection<CraftTransactionItemStack>(outputs.ToArray());
		SemanticsState = semanticsState;
	}

	public int Repetition { get; }
	public IReadOnlyList<CraftActualIngredientQuantity> ActualIngredients { get; }
	public IReadOnlyList<CraftTransactionItemStack> Outputs { get; }
	public object? SemanticsState { get; }
}

public sealed class CraftPreparedRecipeStep
{
	public CraftPreparedRecipeStep(
		ExecutionStepPlan step,
		IReadOnlyList<CraftPreparedRecipeCraft> crafts)
	{
		Step = step;
		Crafts = new ReadOnlyCollection<CraftPreparedRecipeCraft>(crafts.ToArray());
	}

	public ExecutionStepPlan Step { get; }
	public IReadOnlyList<CraftPreparedRecipeCraft> Crafts { get; }
}

public interface ICraftRecipeExecutionSemantics
{
	CraftPreparedRecipeStep PrepareStep(ExecutionStepPlan step);

	void CompleteCraft(
		ExecutionStepPlan step,
		CraftPreparedRecipeCraft craft,
		IReadOnlyList<CraftTransactionItemStack> consumedItems);

	CraftTransactionItemStack Resize(CraftTransactionItemStack item, long quantity);
}

public sealed record CraftPreparedExternalConsumption(
	CraftPreparedSourceAllocation PreparedAllocation,
	long Quantity,
	int StepSequence,
	int IngredientOrdinal,
	int ItemType);

public sealed record CraftOutputCapacityRequest(
	IReadOnlyList<CraftTransactionItemStack> Outputs,
	IReadOnlyList<CraftPreparedExternalConsumption> ExternalConsumptions);

public interface ICraftOutputSink
{
	bool CanAccept(CraftOutputCapacityRequest request, out string? issue);

	IReadOnlyList<CraftTransactionItemStack> Deliver(
		IReadOnlyList<CraftTransactionItemStack> outputs,
		CraftMutationJournal journal,
		Action<int> afterMutation);
}

public interface ICraftTransactionFaultInjector
{
	void ThrowIfRequested(CraftTransactionFaultPoint point, int ordinal);
}

public sealed class NoCraftTransactionFaultInjector : ICraftTransactionFaultInjector
{
	public static NoCraftTransactionFaultInjector Instance { get; } = new();

	private NoCraftTransactionFaultInjector()
	{
	}

	public void ThrowIfRequested(CraftTransactionFaultPoint point, int ordinal)
	{
	}
}

public interface ICraftMutationJournalEntry
{
	string Description { get; }
	void Rollback();
}

public sealed class DelegateCraftMutationJournalEntry : ICraftMutationJournalEntry
{
	private readonly Action _rollback;

	public DelegateCraftMutationJournalEntry(string description, Action rollback)
	{
		if (string.IsNullOrWhiteSpace(description))
			throw new ArgumentException("Journal entry description cannot be empty.", nameof(description));
		Description = description;
		_rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
	}

	public string Description { get; }

	public void Rollback() => _rollback();
}

public sealed class CraftMutationJournal
{
	private readonly List<ICraftMutationJournalEntry> _entries = new();

	public int Count => _entries.Count;

	public void Record(ICraftMutationJournalEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		_entries.Add(entry);
	}

	public IReadOnlyList<Exception> Rollback()
	{
		var failures = new List<Exception>();
		for (int index = _entries.Count - 1; index >= 0; index--) {
			try {
				_entries[index].Rollback();
			}
			catch (Exception exception) {
				failures.Add(new InvalidOperationException(
					$"Rollback journal entry failed: {_entries[index].Description}", exception));
			}
		}
		_entries.Clear();
		return new ReadOnlyCollection<Exception>(failures);
	}

	public void Commit() => _entries.Clear();
}
