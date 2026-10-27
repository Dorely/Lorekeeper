using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

public sealed record McpClientView(Guid Id, string DisplayName, string ClientKind, bool Enabled, string[] Permissions,
    Guid[]? ProjectIds, DateTime? LastUsedAt);

public sealed class McpClientGrantService(IAppDatabaseOperationFactory database, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Lorekeeper.ExternalAccess.Credentials.v1");

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.ExternalAccessSettings.AnyAsync(value => value.Id == 1 && value.Enabled, cancellationToken);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var settings = await operation.Db.ExternalAccessSettings.SingleOrDefaultAsync(value => value.Id == 1, cancellationToken);
        if (settings is null) operation.Db.ExternalAccessSettings.Add(settings = new());
        settings.Enabled = enabled;
        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<McpClientView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return (await operation.Db.McpClientGrants.OrderBy(value => value.DisplayName).ToListAsync(cancellationToken))
            .Select(View).ToArray();
    }

    public async Task<McpClientView> ConfigureAsync(Guid? id, string displayName, string[] permissions,
        Guid[]? projectIds = null, CancellationToken cancellationToken = default, string clientKind = "Other")
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120)
            throw new ArgumentException("Give this connection a name of up to 120 characters.");
        if (permissions.Any(value => !ExternalCapabilities.All.Any(capability => capability.Id == value)))
            throw new ArgumentException("The permission selection contains an unknown capability.");
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var grant = id is Guid existingId
            ? await operation.Db.McpClientGrants.SingleOrDefaultAsync(value => value.Id == existingId, cancellationToken)
                ?? throw new KeyNotFoundException("The client connection was not found.")
            : new McpClientGrant { DisplayName = displayName.Trim(), CredentialHash = "", ProtectedCredential = "", PermissionsJson = "[]" };
        if (projectIds is not null && await operation.Db.Projects.CountAsync(value => projectIds.Contains(value.Id), cancellationToken) != projectIds.Distinct().Count())
            throw new ArgumentException("Select projects that exist in this installation.");
        if (id is null)
        {
            SetCredential(grant);
            operation.Db.McpClientGrants.Add(grant);
        }
        grant.DisplayName = displayName.Trim();
        grant.ClientKind = clientKind is "Codex" or "Claude Code" ? clientKind : "Other MCP client";
        grant.PermissionsJson = JsonSerializer.Serialize(permissions.Distinct().Order(StringComparer.Ordinal));
        grant.ProjectIdsJson = projectIds is null ? null : JsonSerializer.Serialize(projectIds.Distinct());
        grant.Enabled = true;
        grant.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        return View(grant);
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var grant = await operation.Db.McpClientGrants.SingleAsync(value => value.Id == id, cancellationToken);
        grant.Enabled = false;
        grant.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> CredentialForSetupAsync(Guid id, bool rotate = false, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var grant = await operation.Db.McpClientGrants.SingleAsync(value => value.Id == id, cancellationToken);
        if (!grant.Enabled) throw new ExternalAccessException("CLIENT_REVOKED", "Reconnect this client in External Access first.");
        if (rotate) SetCredential(grant);
        string credential;
        try { credential = _protector.Unprotect(grant.ProtectedCredential); }
        catch (CryptographicException)
        {
            SetCredential(grant);
            credential = _protector.Unprotect(grant.ProtectedCredential);
        }
        await operation.SaveChangesAsync(cancellationToken);
        return credential;
    }

    public async Task<McpClientGrant?> AuthenticateAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (credential.Length is < 60 or > 100 || !Guid.TryParseExact(credential.Split('.')[0], "N", out var id)) return null;
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var grant = await operation.Db.McpClientGrants.SingleOrDefaultAsync(value => value.Id == id && value.Enabled, cancellationToken);
        if (grant is null) return null;
        var hash = Hash(credential);
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(grant.CredentialHash), Convert.FromHexString(hash)) ? grant : null;
    }

    public async Task<McpClientGrant> RequireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        if (!await operation.Db.ExternalAccessSettings.AnyAsync(value => value.Id == 1 && value.Enabled, cancellationToken))
            throw new ExternalAccessException("EXTERNAL_ACCESS_DISABLED", "External Access is disabled in Lorekeeper Settings.");
        return await operation.Db.McpClientGrants.SingleOrDefaultAsync(value => value.Id == id && value.Enabled, cancellationToken)
            ?? throw new ExternalAccessException("CLIENT_REVOKED", "This connection has been revoked. Reconnect it in Lorekeeper External Access.");
    }

    internal async Task MarkUsedAsync(Guid id, CancellationToken ct)
    {
        await using var write = await database.OpenWriteAsync(ct);
        var grant = await write.Db.McpClientGrants.SingleOrDefaultAsync(value => value.Id == id && value.Enabled, ct);
        if (grant is not null && (grant.LastUsedAt is null || grant.LastUsedAt < DateTime.UtcNow.AddMinutes(-1)))
        {
            grant.LastUsedAt = DateTime.UtcNow;
            await write.SaveChangesAsync(ct);
        }
    }

    private void SetCredential(McpClientGrant grant)
    {
        var credential = $"{grant.Id:N}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        grant.CredentialHash = Hash(credential);
        grant.ProtectedCredential = _protector.Protect(credential);
        grant.UpdatedAt = DateTime.UtcNow;
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static McpClientView View(McpClientGrant grant) => new(grant.Id, grant.DisplayName, grant.ClientKind, grant.Enabled,
        JsonSerializer.Deserialize<string[]>(grant.PermissionsJson) ?? [],
        grant.ProjectIdsJson is null ? null : JsonSerializer.Deserialize<Guid[]>(grant.ProjectIdsJson), grant.LastUsedAt);
}
