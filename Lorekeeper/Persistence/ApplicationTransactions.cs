using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lorekeeper.Persistence;

/// <summary>Owning services share an enclosing atomic command through SQLite savepoints.</summary>
internal static class ApplicationTransactions
{
    public static async Task<IDbContextTransaction> BeginMutationTransactionAsync(this AppDbContext database, CancellationToken ct)
    {
        if (database.Database.CurrentTransaction is not { } current)
            return await database.Database.BeginTransactionAsync(ct);
        var name = "owner_" + Guid.NewGuid().ToString("N");
        await current.CreateSavepointAsync(name, ct);
        return new Savepoint(current, name);
    }

    private sealed class Savepoint(IDbContextTransaction owner, string name) : IDbContextTransaction, IInfrastructure<DbTransaction>
    {
        private bool _complete;
        public Guid TransactionId => owner.TransactionId;
        public bool SupportsSavepoints => owner.SupportsSavepoints;
        public DbTransaction Instance => owner.GetDbTransaction();
        public void Commit() { if (_complete) return; owner.ReleaseSavepoint(name); _complete = true; }
        public async Task CommitAsync(CancellationToken ct = default) { if (_complete) return; await owner.ReleaseSavepointAsync(name, ct); _complete = true; }
        public void Rollback() { if (_complete) return; owner.RollbackToSavepoint(name); owner.ReleaseSavepoint(name); _complete = true; }
        public async Task RollbackAsync(CancellationToken ct = default) { if (_complete) return; await owner.RollbackToSavepointAsync(name, ct); await owner.ReleaseSavepointAsync(name, ct); _complete = true; }
        public void CreateSavepoint(string savepoint) => owner.CreateSavepoint(savepoint);
        public Task CreateSavepointAsync(string savepoint, CancellationToken ct = default) => owner.CreateSavepointAsync(savepoint, ct);
        public void RollbackToSavepoint(string savepoint) => owner.RollbackToSavepoint(savepoint);
        public Task RollbackToSavepointAsync(string savepoint, CancellationToken ct = default) => owner.RollbackToSavepointAsync(savepoint, ct);
        public void ReleaseSavepoint(string savepoint) => owner.ReleaseSavepoint(savepoint);
        public Task ReleaseSavepointAsync(string savepoint, CancellationToken ct = default) => owner.ReleaseSavepointAsync(savepoint, ct);
        public void Dispose() => Rollback();
        public async ValueTask DisposeAsync() => await RollbackAsync();
    }
}
