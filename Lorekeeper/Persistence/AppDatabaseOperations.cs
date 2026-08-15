using Lorekeeper.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence;

public interface IAppDatabaseOperationFactory
{
    AppDatabaseReadOperation OpenRead();
    AppDatabaseWriteOperation OpenWrite();
    ValueTask<AppDatabaseReadOperation> OpenReadAsync(CancellationToken cancellationToken = default);
    ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(CancellationToken cancellationToken = default);
    ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

public interface IAppDatabaseWriteCoordinator
{
    IDisposable Acquire();
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

public sealed class AppDatabaseWriteCoordinator : IAppDatabaseWriteCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IDisposable Acquire()
    {
        if (AppDatabaseOperationAmbient.Current is not null)
            return NoopLease.Instance;

        _gate.Wait();
        return new Releaser(_gate);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (AppDatabaseOperationAmbient.Current is not null)
            return NoopLease.Instance;

        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class NoopLease : IDisposable, IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable, IAsyncDisposable
    {
        private bool _released;

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                gate.Release();
            }
        }
    }
}

public sealed class AppDatabaseOperationFactory(
    IDbContextFactory<AppDbContext> contextFactory,
    IAppDatabaseWriteCoordinator writeCoordinator,
    IProjectMutationCoordinator projectMutations) : IAppDatabaseOperationFactory
{
    public AppDatabaseReadOperation OpenRead() => AppDatabaseOperationAmbient.Current is { } current
        ? new AppDatabaseReadOperation(current.Db, ownsContext: false)
        : new AppDatabaseReadOperation(contextFactory.CreateDbContext());

    public AppDatabaseWriteOperation OpenWrite()
    {
        if (AppDatabaseOperationAmbient.Current is { } current)
            return AppDatabaseWriteOperation.Borrow(current.Db);

        var lease = writeCoordinator.Acquire();
        try
        {
            return new AppDatabaseWriteOperation(contextFactory.CreateDbContext(), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async ValueTask<AppDatabaseReadOperation> OpenReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (AppDatabaseOperationAmbient.Current is { } current)
            return new AppDatabaseReadOperation(current.Db, ownsContext: false);

        return new AppDatabaseReadOperation(await contextFactory.CreateDbContextAsync(cancellationToken));
    }

    public async ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(
        CancellationToken cancellationToken = default)
    {
        if (AppDatabaseOperationAmbient.Current is { } current)
            return AppDatabaseWriteOperation.Borrow(current.Db);

        var lease = await writeCoordinator.AcquireAsync(cancellationToken);
        try
        {
            var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            return new AppDatabaseWriteOperation(db, lease);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (AppDatabaseOperationAmbient.Current is { } current)
            return AppDatabaseWriteOperation.Borrow(current.Db);

        var projectLease = await projectMutations.AcquireAsync(projectId, cancellationToken);
        try
        {
            var writeLease = await writeCoordinator.AcquireAsync(cancellationToken);
            try
            {
                var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                return new AppDatabaseWriteOperation(
                    db,
                    new OrderedWriteLease(writeLease, projectLease));
            }
            catch
            {
                await writeLease.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await projectLease.DisposeAsync();
            throw;
        }
    }

    private sealed class OrderedWriteLease(
        IAsyncDisposable databaseLease,
        IAsyncDisposable projectLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await databaseLease.DisposeAsync();
            }
            finally
            {
                await projectLease.DisposeAsync();
            }
        }
    }
}

internal static class AppDatabaseOperationAmbient
{
    public static readonly AsyncLocal<AppDatabaseWriteOperation?> Slot = new();

    public static AppDatabaseWriteOperation? Current
    {
        get
        {
            var current = Slot.Value;
            while (current?.IsDisposed == true)
                current = current.PreviousAmbient;
            if (!ReferenceEquals(current, Slot.Value))
                Slot.Value = current;
            return current;
        }
    }
}

public class AppDatabaseReadOperation : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsContext;
    private bool _disposed;

    public AppDatabaseReadOperation(AppDbContext db, bool ownsContext = true)
    {
        Db = db;
        _ownsContext = ownsContext;
        Repositories = new DatabaseRepositories(this);
    }

    public AppDbContext Db { get; }
    public DatabaseRepositories Repositories { get; }

    public virtual void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsContext)
            Db.Dispose();
    }

    public virtual ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        return _ownsContext
            ? Db.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}

public sealed class AppDatabaseWriteOperation(
    AppDbContext db,
    object? writeLease,
    bool ownsContext = true) : AppDatabaseReadOperation(db, ownsContext), IDisposable
{
    private bool _disposed;
    private bool _ambientEnabled;
    private AppDatabaseWriteOperation? _previousAmbient;

    internal bool IsDisposed => _disposed;
    internal AppDatabaseWriteOperation? PreviousAmbient => _previousAmbient;

    public AppDatabaseWriteOperation(AppDbContext db, IDisposable writeLease)
        : this(db, (object)writeLease, ownsContext: true)
    {
        Db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
    }

    public AppDatabaseWriteOperation(AppDbContext db, IAsyncDisposable writeLease)
        : this(db, (object)writeLease, ownsContext: true)
    {
        Db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        Db.SaveChangesAsync(cancellationToken);

    internal static AppDatabaseWriteOperation Borrow(AppDbContext db) => new(db, writeLease: null, ownsContext: false);

    public void ShareWithNestedOperations()
    {
        if (_ambientEnabled)
            return;

        _ambientEnabled = true;
        _previousAmbient = AppDatabaseOperationAmbient.Current;
        AppDatabaseOperationAmbient.Slot.Value = this;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            base.Dispose();
        }
        finally
        {
            RestoreAmbient();
            if (writeLease is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        RestoreAmbient();
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (writeLease is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (writeLease is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private void RestoreAmbient()
    {
        if (!_ambientEnabled)
            return;

        AppDatabaseOperationAmbient.Slot.Value = _previousAmbient;
        _ambientEnabled = false;
    }
}
