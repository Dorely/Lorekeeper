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
    void CompleteDirectMutation(AppDbContext db, string commandText);
    void PrepareFreshMutation<TEntity>(AppDbContext db, Func<TEntity, bool> predicate)
        where TEntity : class;
    void BeginSave(AppDbContext db);
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
    private long _generation;

    public void BeforeCommand(AppDbContext db, string commandText)
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
        }
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
            var pending = entries
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(entry => entry.Metadata.ClrType.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (pending.Length > 0)
            {
                throw new InvalidOperationException(
                    $"A previous {string.Join(", ", pending)} mutation has not finished saving. Wait for it to complete and retry.");
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
        var state = State(db);
        lock (state.Gate)
        {
            if (state.IsSaving)
                throw new InvalidOperationException("A database save is already active on this persistence context.");
            var tables = ChangedTables(db);
            RefreshTrackedReads(db, state, tables);
            state.SavingTables = tables;
            state.IsSaving = true;
        }
    }

    public void CompleteSave(AppDbContext db, int savedEntries)
    {
        var state = State(db);
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
        }
    }

    public void FailSave(AppDbContext db)
    {
        var state = State(db);
        lock (state.Gate)
        {
            state.SavingTables = [];
            state.IsSaving = false;
            db.ChangeTracker.Clear();
        }
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

    private sealed class ContextState
    {
        public object Gate { get; } = new();
        public Dictionary<string, long> SeenGenerations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SavingTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PendingTransactionTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSaving { get; set; }
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

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Before(eventData, command);
        return ValueTask.FromResult(result);
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

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Before(eventData, command);
        return ValueTask.FromResult(result);
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

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Before(eventData, command);
        return ValueTask.FromResult(result);
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
}
