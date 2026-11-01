namespace IssueLab.VoiceBench.Core;

public sealed record AuthoringTransactionRecord(
    Guid TransactionId,
    string Label,
    DateTimeOffset CreatedAt,
    string BeforeStateSha256,
    string AfterStateSha256);

public sealed record AuthoringTransactionState(
    int SchemaVersion,
    IReadOnlyList<AuthoringTransactionRecord> Transactions,
    int Cursor,
    bool HasInterruptedTransaction)
{
    public const int CurrentSchemaVersion = 1;

    public bool CanUndo => Cursor > 0;

    public bool CanRedo => Cursor < Transactions.Count;

    public AuthoringTransactionRecord? NextUndo => CanUndo ? Transactions[Cursor - 1] : null;

    public AuthoringTransactionRecord? NextRedo => CanRedo ? Transactions[Cursor] : null;
}

public interface IAuthoringTransactionManager
{
    Task<AuthoringTransactionState> GetStateAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string projectRoot,
        string label,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    Task<AuthoringTransactionRecord> UndoAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<AuthoringTransactionRecord> RedoAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<bool> RecoverInterruptedAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);
}
