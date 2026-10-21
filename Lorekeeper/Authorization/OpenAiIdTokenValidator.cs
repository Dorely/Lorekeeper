using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lorekeeper.Authorization;

public sealed record OpenAiIdTokenIdentity(string Subject, string? Email);

/// <summary>
/// Verifies ChatGPT plan ID tokens: RS256 against OpenAI's published JWKS, the
/// OpenAI issuer, the issued client as audience, expiry, and the per-attempt
/// nonce. Keys are cached for the process and refetched once for an unknown key.
/// </summary>
public sealed class OpenAiIdTokenValidator(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider)
{
    public const string Issuer = "https://auth.openai.com";
    public const string JwksUri = "https://auth.openai.com/.well-known/jwks.json";

    private const int MaxJwksBytes = 256 * 1024;
    private static readonly TimeSpan KeyCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan JwksTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _keysFetchedAt = DateTimeOffset.MinValue;

    public async Task<OpenAiIdTokenIdentity> ValidateAsync(
        string idToken,
        string audience,
        string nonce,
        CancellationToken cancellationToken = default)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw Invalid();

        using var header = ParseSegment(parts[0]);
        using var payload = ParseSegment(parts[1]);
        var signature = DecodeSegment(parts[2]);

        if (ReadString(header.RootElement, "alg") != "RS256"
            || ReadString(header.RootElement, "kid") is not { Length: > 0 } keyId)
        {
            throw Invalid();
        }

        var key = await FindKeyAsync(keyId, cancellationToken) ?? throw Invalid();
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw Invalid();
        }

        var claims = payload.RootElement;
        if (!string.Equals(ReadString(claims, "iss"), Issuer, StringComparison.Ordinal)
            || !HasAudience(claims, audience)
            || ReadString(claims, "sub") is not { Length: > 0 } subject
            || ReadString(claims, "nonce") is not { } tokenNonce
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(nonce)))
        {
            throw Invalid();
        }

        var now = timeProvider.GetUtcNow();
        if (!claims.TryGetProperty("exp", out var exp)
            || !exp.TryGetInt64(out var expiresAt)
            || DateTimeOffset.FromUnixTimeSeconds(expiresAt) <= now - ClockSkew)
        {
            throw Invalid();
        }

        return new OpenAiIdTokenIdentity(subject, ReadString(claims, "email"));
    }

    private static bool HasAudience(JsonElement claims, string audience)
    {
        if (!claims.TryGetProperty("aud", out var aud))
            return false;

        if (aud.ValueKind == JsonValueKind.String)
            return string.Equals(aud.GetString(), audience, StringComparison.Ordinal);

        if (aud.ValueKind != JsonValueKind.Array)
            return false;

        var audiences = aud.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .ToList();
        if (!audiences.Contains(audience, StringComparer.Ordinal))
            return false;

        // With several audiences, the authorized party must be this client.
        return audiences.Count == 1
            || string.Equals(ReadString(claims, "azp"), audience, StringComparison.Ordinal);
    }

    private async Task<RSAParameters?> FindKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var stale = now - _keysFetchedAt > KeyCacheLifetime;
            if (!stale && _keys.TryGetValue(keyId, out var cached))
                return cached;

            // Refetch when the cache is stale or the key is new, at most once a minute.
            if (stale || now - _keysFetchedAt > TimeSpan.FromMinutes(1))
            {
                _keys = await FetchKeysAsync(cancellationToken);
                _keysFetchedAt = now;
            }

            return _keys.TryGetValue(keyId, out var key) ? key : null;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private async Task<Dictionary<string, RSAParameters>> FetchKeysAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(JwksTimeout);
        using var response = await httpClientFactory.CreateClient().GetAsync(
            JwksUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("OpenAI signing keys could not be read.", null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > MaxJwksBytes)
                throw new InvalidOperationException("OpenAI signing keys were too large to read.");
            buffer.Write(chunk, 0, read);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("keys", out var keyArray) || keyArray.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (var jwk in keyArray.EnumerateArray())
        {
            if (ReadString(jwk, "kty") != "RSA"
                || ReadString(jwk, "kid") is not { Length: > 0 } kid
                || ReadString(jwk, "use") is { } use && use != "sig"
                || ReadString(jwk, "alg") is { } alg && alg != "RS256"
                || ReadString(jwk, "n") is not { Length: > 0 } modulus
                || ReadString(jwk, "e") is not { Length: > 0 } exponent)
            {
                continue;
            }

            keys[kid] = new RSAParameters
            {
                Modulus = DecodeSegment(modulus),
                Exponent = DecodeSegment(exponent),
            };
        }

        return keys;
    }

    private static JsonDocument ParseSegment(string segment)
    {
        try
        {
            return JsonDocument.Parse(DecodeSegment(segment));
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static byte[] DecodeSegment(string segment)
    {
        try
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static OpenAiAuthorizationException Invalid() =>
        new("OpenAI returned an identity token that could not be verified.");
}
