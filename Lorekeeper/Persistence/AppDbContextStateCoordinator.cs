using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lorekeeper.Persistence;

public interface IAppDbContextStateCoordinator
{
    void BeforeCommand(AppDbContext db, string commandText);
    ValueTask BeforeCommandAsync(AppDbContext db, string commandText, CancellationToken cancellationToken);
    void CompleteDirectMutation(AppDbContext db, string commandText);
    void FailCommand(AppDbContext db);
    void PrepareFreshMutation<TEntity>(AppDbContext db, Func<TEntity, bool> predicate)
        where TEntity : class;
    void BeginSave(AppDbContext db);
    ValueTask BeginSaveAsync(AppDbContext db, CancellationToken cancellationToken);
    void CompleteSave(AppDbContext db, int savedEntries);
    void FailSave(AppDbContext db);
    void CompleteTransaction(AppDbContext db);
    void RollbackTransaction(AppDbContext db);
}

/// <summary>
/// Keeps every DI-created context coherent even though Blazor circuit scopes live longer
/// than an individual persistence operation. Table generations invalidate tracked reads;
/// bulk mutations invalidate matching tracked reads before a later UI event can reuse them.
/// Row-level write reconciliation belongs to <see cref="AppDbContext"/>, not this table cache.
/// </summary>
public sealed class AppDbContextStateCoordinator(ILogger<AppDbContextStateCoordinator> logger)
    : IAppDbContextStateCoordinator
{
    private static readonly Regex _queryTablePattern = new(
        "\\b(?:FROM|JOIN)\\s+(?:\\\"(?<quoted>[^\\\"]+)\\\"|\\[(?<bracketed>[^\\]]+)\\]|(?<bare>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _mutationTablePattern = new(
        "\\b(?:UPDATE|INSERT\\s+INTO|DELETE\\s+FROM|REPLACE\\s+INTO)\\s+(?:\\\"(?<quoted>[^\\\"]+)\\\"|\\[(?<bracketed>[^\\]]+)\\]|(?<bare>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, long> _tableGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<AppDbContext, ContextState> _contexts = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private long _generation;

    public void BeforeCommand(AppDbContext db, string commandText)
    {
        var mutationTables = MutationTables(commandText);
        var enteredMutation = mutationTables.Count > 0 && !IsSaving(db);
        if (enteredMutation)
            EnterDirectMutation(db);
        try
        {
            PrepareCommand(db, commandText);
        }
        catch
        {
            if (enteredMutation)
                FailCommand(db);
            throw;
        }
    }

    public async ValueTask BeforeCommandAsync(
        AppDbContext db,
        string commandText,
        CancellationToken cancellationToken)
    {
        var mutationTables = MutationTables(commandText);
        var enteredMutation = mutationTables.Count > 0 && !IsSaving(db);
        if (enteredMutation)
            await EnterDirectMutationAsync(db, cancellationToken);
        try
        {
            PrepareCommand(db, commandText);
        }
        catch
        {
            if (enteredMutation)
                FailCommand(db);
            throw;
        }
    }

    private void PrepareCommand(AppDbContext db, string commandText)
    {
        var state = State(db);
        lock (state.Gate)
        {
            if (state.IsSaving)
                return;
            RefreshTrackedReads(db, state, ReadTables(commandText));
        }
    }

    public void CompleteDirectMutation(AppDbContext db, string commandText)
    {
        var tables = MutationTables(commandText);
        if (tables.Count == 0)
            return;
        var state = State(db);
        var releaseLease = false;
        lock (state.Gate)
        {
            if (state.IsSaving)
                return;
            foreach (var table in tables)
            {
                DetachUnchanged(db, table);
                if (db.Database.CurrentTransaction is not null)
                    state.PendingTransactionTables.Add(table);
                else
                    state.SeenGenerations[table] = NextGeneration(table);
            }
            releaseLease = state.HasDirectMutationLease;
            state.HasDirectMutationLease = false;
        }
        if (releaseLease)
            _mutationGate.Release();
    }

    public void FailCommand(AppDbContext db)
    {
        var state = State(db);
        var releaseLease = false;
        lock (state.Gate)
        {
            if (!state.IsSaving && state.HasDirectMutationLease)
            {
                state.HasDirectMutationLease = false;
                releaseLease = true;
            }
        }
        if (releaseLease)
            _mutationGate.Release();
    }

    public void PrepareFreshMutation<TEntity>(AppDbContext db, Func<TEntity, bool> predicate)
        where TEntity : class
    {
        var state = State(db);
        lock (state.Gate)
        {
            if (state.IsSaving)
                throw new InvalidOperationException("A fresh mutation cannot begin while this persistence context is saving.");
            var entries = db.ChangeTracker.Entries<TEntity>()
                .Where(entry => predicate(entry.Entity))
                .ToList();
            var abandoned = entries
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();
            if (abandoned.Count > 0)
            {
                var entityNames = db.ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    .Select(entry => entry.Metadata.ClrType.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                db.ChangeTracker.Clear();
                state.SeenGenerations.Clear();
                logger.LogWarning(
                    "Discarded abandoned tracked state for {Entities} before loading the next serialized mutation. The prior operation did not commit it.",
                    string.Join(", ", entityNames));
                return;
            }
            foreach (var entry in entries.Where(entry => entry.State == EntityState.Unchanged))
            {
                var table = entry.Metadata.GetTableName();
                entry.State = EntityState.Detached;
                if (!string.IsNullOrWhiteSpace(table))
                    state.SeenGenerations[table] = _tableGenerations.GetValueOrDefault(table);
            }
        }
    }

    public void BeginSave(AppDbContext db)
    {
        _mutationGate.Wait();
        try
        {
            BeginSaveWithLease(db);
        }
        catch
        {
            _mutationGate.Release();
            throw;
        }
    }

    public async ValueTask BeginSaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            BeginSaveWithLease(db);
        }
        catch
        {
            _mutationGate.Release();
            throw;
        }
    }

    private void BeginSaveWithLease(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
        {
            if (state.IsSaving)
                throw new InvalidOperationException("A database save is already active on this persistence context.");
            var tables = ChangedTables(db);
            RefreshTrackedReads(db, state, tables);
            state.SavingTables = tables;
            state.IsSaving = true;
            state.HasSaveLease = true;
        }
    }

    public void CompleteSave(AppDbContext db, int savedEntries)
    {
        var state = State(db);
        var releaseLease = false;
        lock (state.Gate)
        {
            if (savedEntries > 0)
            {
                foreach (var table in state.SavingTables)
                {
                    if (db.Database.CurrentTransaction is not null)
                        state.PendingTransactionTables.Add(table);
                    else
                        state.SeenGenerations[table] = NextGeneration(table);
                }
            }
            state.SavingTables = [];
            state.IsSaving = false;
            releaseLease = state.HasSaveLease;
            state.HasSaveLease = false;
        }
        if (releaseLease)
            _mutationGate.Release();
    }

    public void FailSave(AppDbContext db)
    {
        var state = State(db);
        var releaseLease = false;
        lock (state.Gate)
        {
            state.SavingTables = [];
            state.IsSaving = false;
            releaseLease = state.HasSaveLease;
            state.HasSaveLease = false;
            db.ChangeTracker.Clear();
        }
        if (releaseLease)
            _mutationGate.Release();
    }

    public void CompleteTransaction(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
        {
            foreach (var table in state.PendingTransactionTables)
                state.SeenGenerations[table] = NextGeneration(table);
            state.PendingTransactionTables.Clear();
        }
    }

    public void RollbackTransaction(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
            state.PendingTransactionTables.Clear();
    }

    private void RefreshTrackedReads(AppDbContext db, ContextState state, IReadOnlySet<string> tables)
    {
        foreach (var table in tables)
        {
            var current = _tableGenerations.GetValueOrDefault(table);
            var seen = state.SeenGenerations.GetValueOrDefault(table);
            if (current <= seen)
                continue;
            var detached = DetachUnchanged(db, table);
            state.SeenGenerations[table] = current;
            if (detached > 0)
            {
                logger.LogDebug(
                    "Discarded {Count} stale tracked {Table} entities before the next persistence operation.",
                    detached,
                    table);
            }
        }
    }

    private long NextGeneration(string table)
    {
        var generation = Interlocked.Increment(ref _generation);
        _tableGenerations[table] = generation;
        return generation;
    }

    private static int DetachUnchanged(AppDbContext db, string table)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Unchanged && IsTable(entry, table))
            .ToList();
        foreach (var entry in entries)
            entry.State = EntityState.Detached;
        return entries.Count;
    }

    private static HashSet<string> ChangedTables(AppDbContext db) => db.ChangeTracker.Entries()
        .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        .Select(entry => entry.Metadata.GetTableName())
        .Where(table => !string.IsNullOrWhiteSpace(table))
        .Select(table => table!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsTable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string table) =>
        string.Equals(entry.Metadata.GetTableName(), table, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ReadTables(string commandText)
    {
        var tables = QueryTables(commandText);
        tables.UnionWith(MutationTables(commandText));
        return tables;
    }

    private static HashSet<string> QueryTables(string commandText) =>
        Tables(_queryTablePattern.Matches(commandText));

    private static HashSet<string> MutationTables(string commandText) =>
        Tables(_mutationTablePattern.Matches(commandText));

    private static HashSet<string> Tables(MatchCollection matches)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var table = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bracketed"].Success
                    ? match.Groups["bracketed"].Value
                    : match.Groups["bare"].Value;
            if (!string.IsNullOrWhiteSpace(table))
                tables.Add(table);
        }
        return tables;
    }

    private ContextState State(AppDbContext db) => _contexts.GetValue(db, _ => new ContextState());

    private bool IsSaving(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
            return state.IsSaving;
    }

    private void EnterDirectMutation(AppDbContext db)
    {
        _mutationGate.Wait();
        MarkDirectMutationLease(db);
    }

    private async ValueTask EnterDirectMutationAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        MarkDirectMutationLease(db);
    }

    private void MarkDirectMutationLease(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
        {
            if (state.IsSaving || state.HasDirectMutationLease)
            {
                _mutationGate.Release();
                if (state.HasDirectMutationLease)
                    throw new InvalidOperationException("A direct database mutation is already active on this persistence context.");
                return;
            }
            state.HasDirectMutationLease = true;
        }
    }

    private sealed class ContextState
    {
        public object Gate { get; } = new();
        public Dictionary<string, long> SeenGenerations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SavingTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PendingTransactionTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSaving { get; set; }
        public bool HasSaveLease { get; set; }
        public bool HasDirectMutationLease { get; set; }
    }
}

public sealed class AppDbContextTransactionInterceptor(IAppDbContextStateCoordinator coordinator)
    : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.CompleteTransaction(db);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.CompleteTransaction(db);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.RollbackTransaction(db);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.RollbackTransaction(db);
        return Task.CompletedTask;
    }
}

public sealed class AppDbContextCommandInterceptor(IAppDbContextStateCoordinator coordinator)
    : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Before(eventData, command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await BeforeAsync(eventData, command, cancellationToken);
        return result;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        After(eventData, command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        After(eventData, command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Before(eventData, command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await BeforeAsync(eventData, command, cancellationToken);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        After(eventData, command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        After(eventData, command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Before(eventData, command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await BeforeAsync(eventData, command, cancellationToken);
        return result;
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        After(eventData, command);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        After(eventData, command);
        return ValueTask.FromResult(result);
    }

    private void Before(CommandEventData eventData, DbCommand command)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.BeforeCommand(db, command.CommandText);
    }

    private void After(CommandEndEventData eventData, DbCommand command)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.CompleteDirectMutation(db, command.CommandText);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.FailCommand(db);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            coordinator.FailCommand(db);
        return Task.CompletedTask;
    }

    private ValueTask BeforeAsync(
        CommandEventData eventData,
        DbCommand command,
        CancellationToken cancellationToken) =>
        eventData.Context is AppDbContext db
            ? coordinator.BeforeCommandAsync(db, command.CommandText, cancellationToken)
            : ValueTask.CompletedTask;
}
