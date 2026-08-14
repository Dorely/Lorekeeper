using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Lorekeeper.Manuscripts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence;

public interface IDatabaseMigrationRecoveryService
{
    Task<bool> ApplyScheduledRestoreAsync(CancellationToken cancellationToken = default);
    Task<string> CreateBackupAsync(
        string category,
        string purpose,
        CancellationToken cancellationToken = default);
    Task EnterRecoveryModeAsync(
        AppDbContext db,
        string backupPath,
        string migrationName,
        int sourceVersion,
        int targetVersion,
        Exception exception,
        CancellationToken cancellationToken = default);
    Task<DatabaseMigrationRecoveryState> GetStateAsync(
        CancellationToken cancellationToken = default);
    Task<bool> IsRecoveryRequiredAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManuscriptBackupInfo>> ListBackupsAsync(
        CancellationToken cancellationToken = default);
    Task<ManuscriptRestoreRequest> PrepareRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default);
    Task ScheduleRestoreAsync(
        string backupPath,
        string confirmationToken,
        CancellationToken cancellationToken = default);
    Task PruneAutomaticBackupsAsync(
        string category,
        int maximum,
        IReadOnlyCollection<string> protectedBackupPaths,
        CancellationToken cancellationToken = default);
}

public sealed record DatabaseMigrationRecoveryState(
    bool RecoveryRequired,
    string? MigrationName,
    int? SourceVersion,
    int? TargetVersion,
    string? BackupPath,
    string? Error,
    DateTime? CreatedAtUtc);

public sealed class DatabaseMigrationRecoveryService(
    IConfiguration configuration,
    ILogger<DatabaseMigrationRecoveryService> logger) : IDatabaseMigrationRecoveryService
{
    private static readonly TimeSpan RestoreTokenLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (string Path, DateTime ExpiresAt)> _restoreTokens = [];
    private readonly object _restoreTokenLock = new();
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task<bool> ApplyScheduledRestoreAsync(CancellationToken cancellationToken = default)
    {
        var markerPath = RestoreMarkerPath();
        if (!File.Exists(markerPath))
            return false;

        ScheduledRestore request;
        try
        {
            request = JsonSerializer.Deserialize<ScheduledRestore>(
                await File.ReadAllTextAsync(markerPath, cancellationToken))
                ?? throw new InvalidDataException("The scheduled restore marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The scheduled restore marker is malformed.", exception);
        }

        var backupPath = ValidateBackupPath(request.BackupPath);
        await EnsureHealthyAsync($"Data Source={backupPath}", cancellationToken);
        if (File.Exists(DatabasePath()))
            _ = await CreateBackupAsync("recovery", "pre-restore-diagnostic", cancellationToken);
        await RestoreDatabaseAsync(backupPath, cancellationToken);
        File.Delete(markerPath);
        if (File.Exists(RecoveryStatePath()))
            File.Delete(RecoveryStatePath());
        return true;
    }

    public async Task<string> CreateBackupAsync(
        string category,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var safeCategory = SafeSegment(category, nameof(category));
        var safePurpose = SafeSegment(purpose, nameof(purpose));
        var directory = Path.Combine(BackupRoot(), safeCategory);
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safePurpose}.db");
        await using var source = await OpenAsync(_connectionString, cancellationToken);
        await using var destination = await OpenAsync($"Data Source={path}", cancellationToken);
        source.BackupDatabase(destination);
        RestrictFile(path);
        await EnsureHealthyAsync($"Data Source={path}", cancellationToken);
        return path;
    }

    public async Task EnterRecoveryModeAsync(
        AppDbContext db,
        string backupPath,
        string migrationName,
        int sourceVersion,
        int targetVersion,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        var resolved = ValidateBackupPath(backupPath);
        await RestoreDatabaseAsync(resolved, cancellationToken);
        db.ChangeTracker.Clear();
        await ClearStrandedMigrationLockAsync(db, cancellationToken);
        await DatabaseStartupMigrationService.RemoveEditionCompatibilityColumnsAsync(db, cancellationToken);
        await DatabaseStartupMigrationService.RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await DatabaseStartupMigrationService.RemovePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await DatabaseStartupMigrationService.RemoveAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Projects;", cancellationToken);
        db.ChangeTracker.Clear();
        await EnsureHealthyAsync(_connectionString, cancellationToken);

        var state = new DatabaseRecoveryState(
            migrationName,
            sourceVersion,
            targetVersion,
            resolved,
            DescribeException(exception),
            DateTime.UtcNow);
        var temporaryPath = RecoveryStatePath() + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(state),
            cancellationToken);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, RecoveryStatePath(), overwrite: true);
        RestrictFile(RecoveryStatePath());
        logger.LogWarning(
            "Migration {MigrationName} entered recovery mode with protected backup {BackupPath}.",
            migrationName,
            resolved);
    }

    public async Task<DatabaseMigrationRecoveryState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(RecoveryStatePath()))
            return new(false, null, null, null, null, null, null);
        try
        {
            var state = JsonSerializer.Deserialize<DatabaseRecoveryState>(
                await File.ReadAllTextAsync(RecoveryStatePath(), cancellationToken))
                ?? throw new InvalidDataException("The migration recovery-state marker is empty.");
            return new(
                true,
                state.MigrationName,
                state.SourceVersion,
                state.TargetVersion,
                state.BackupPath,
                state.Error,
                state.CreatedAtUtc);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The migration recovery-state marker is malformed.", exception);
        }
    }

    private static string DescribeException(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 4; current = current.InnerException)
        {
            var part = $"{current.GetType().Name}: {current.Message}";
            if (!parts.Contains(part, StringComparer.Ordinal))
                parts.Add(part);
        }
        return string.Join(" -> ", parts);
    }

    public async Task<bool> IsRecoveryRequiredAsync(CancellationToken cancellationToken = default) =>
        (await GetStateAsync(cancellationToken)).RecoveryRequired;

    public Task<IReadOnlyList<ManuscriptBackupInfo>> ListBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = BackupRoot();
        IReadOnlyList<ManuscriptBackupInfo> result = !Directory.Exists(root)
            ? []
            : Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Select(file => new ManuscriptBackupInfo(file.FullName, file.Length, file.CreationTimeUtc))
                .ToList();
        return Task.FromResult(result);
    }

    public Task<ManuscriptRestoreRequest> PrepareRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ValidateBackupPath(backupPath);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTime.UtcNow.Add(RestoreTokenLifetime);
        lock (_restoreTokenLock)
            _restoreTokens[token] = (resolved, expires);
        return Task.FromResult(new ManuscriptRestoreRequest(resolved, token, expires));
    }

    public async Task ScheduleRestoreAsync(
        string backupPath,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        var resolved = ValidateBackupPath(backupPath);
        lock (_restoreTokenLock)
        {
            if (!_restoreTokens.Remove(confirmationToken, out var request)
                || request.ExpiresAt < DateTime.UtcNow
                || !string.Equals(request.Path, resolved, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The restore confirmation is invalid or expired.");
            }
        }

        await EnsureHealthyAsync($"Data Source={resolved}", cancellationToken);
        var markerPath = RestoreMarkerPath();
        var temporaryPath = markerPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(new ScheduledRestore(resolved)),
            cancellationToken);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, markerPath, overwrite: true);
        RestrictFile(markerPath);
    }

    public async Task PruneAutomaticBackupsAsync(
        string category,
        int maximum,
        IReadOnlyCollection<string> protectedBackupPaths,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        var directory = Path.GetFullPath(Path.Combine(BackupRoot(), SafeSegment(category, nameof(category))));
        var protectedPaths = protectedBackupPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scheduled = await ReadScheduledRestorePathAsync(cancellationToken);
        if (scheduled is not null)
            protectedPaths.Add(scheduled);
        var recovery = await ReadRecoveryBackupPathAsync(cancellationToken);
        if (recovery is not null)
            protectedPaths.Add(recovery);
        if (!Directory.Exists(directory))
            return;
        SqliteConnection.ClearAllPools();
        var automatic = Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => !file.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(maximum)
            .Where(file => !protectedPaths.Contains(file.FullName))
            .ToList();
        foreach (var backup in automatic)
            File.Delete(backup.FullName);
    }

    private async Task RestoreDatabaseAsync(string backupPath, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using var source = await OpenAsync($"Data Source={backupPath};Mode=ReadOnly", cancellationToken);
        await using var destination = await OpenAsync(_connectionString, cancellationToken);
        source.BackupDatabase(destination);
        await EnsureHealthyAsync(_connectionString, cancellationToken);
    }

    private async Task<string?> ReadScheduledRestorePathAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RestoreMarkerPath()))
            return null;
        try
        {
            var marker = JsonSerializer.Deserialize<ScheduledRestore>(
                await File.ReadAllTextAsync(RestoreMarkerPath(), cancellationToken));
            return marker is null ? null : Path.GetFullPath(marker.BackupPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ReadRecoveryBackupPathAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RecoveryStatePath()))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<DatabaseRecoveryState>(
                await File.ReadAllTextAsync(RecoveryStatePath(), cancellationToken));
            return state is null ? null : Path.GetFullPath(state.BackupPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task ClearStrandedMigrationLockAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\";", cancellationToken);
        }
        catch
        {
            // A fresh or older protected backup may not have the lock table.
        }
    }

    private string ValidateBackupPath(string path)
    {
        var resolved = Path.GetFullPath(path);
        var root = Path.GetFullPath(BackupRoot()) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new InvalidOperationException("The requested file is not a protected Lorekeeper migration backup.");
        return resolved;
    }

    private string DatabasePath() =>
        Path.GetFullPath(new SqliteConnectionStringBuilder(_connectionString).DataSource);

    private string BackupRoot()
    {
        var root = Path.Combine(Path.GetDirectoryName(DatabasePath())!, ".migration-backups");
        Directory.CreateDirectory(root);
        RestrictDirectory(root);
        return root;
    }

    private string RestoreMarkerPath() => Path.Combine(BackupRoot(), "scheduled-restore.json");

    private string RecoveryStatePath() => Path.Combine(BackupRoot(), "recovery-state.json");

    private static string SafeSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Migration backup path segments may contain only ASCII letters, digits, and hyphens.", parameterName);
        }
        return value;
    }

    private static async Task EnsureHealthyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite quick_check failed: {result}");
    }

    private static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record ScheduledRestore(string BackupPath);

    private sealed record DatabaseRecoveryState(
        string MigrationName,
        int SourceVersion,
        int TargetVersion,
        string BackupPath,
        string Error,
        DateTime CreatedAtUtc);
}
