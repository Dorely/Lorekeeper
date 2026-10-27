using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Lorekeeper.ExternalAccess;

internal sealed record ExternalLocalConnection(string Endpoint, string ProtectedCredential);

/// <summary>Noninteractive binary handoff; runs without starting another application host.</summary>
internal static class ExternalTransferCommand
{
    internal static string SecurityRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lorekeeper", "ExternalAccess", "security");
    internal static string KeysPath => Path.Combine(SecurityRoot, "keys");

    public static async Task<int> RunAsync(string[] arguments)
    {
        string? temporary = null;
        try
        {
            if (arguments.Length != 5 || arguments[0] is not ("upload" or "download"))
            {
                Console.Error.WriteLine("Usage: --external-transfer upload|download <private-connection-file> <resource> <local-file> <maximum-bytes>");
                return 2;
            }
            var connection = JsonSerializer.Deserialize<ExternalLocalConnection>(await File.ReadAllTextAsync(arguments[1]), ExternalToolCatalog.Json)
                ?? throw new InvalidDataException();
            var endpoint = new Uri(connection.Endpoint);
            if (!endpoint.IsLoopback || endpoint.Scheme != "http" || !arguments[2].StartsWith("/external-access/transfers/", StringComparison.Ordinal)
                || arguments[2].Contains("..", StringComparison.Ordinal) || arguments[2].Contains('\\')) throw new InvalidDataException();
            var resource = new Uri(endpoint, arguments[2]);
            if (resource.Authority != endpoint.Authority || resource.Scheme != endpoint.Scheme) throw new InvalidDataException();
            var maximumBytes = long.Parse(arguments[4], System.Globalization.CultureInfo.InvariantCulture);
            if (maximumBytes is <= 0 or > 536870912) throw new InvalidDataException();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
            var provider = DataProtectionProvider.Create(new DirectoryInfo(KeysPath), options => options.SetApplicationName("Lorekeeper.ExternalAccess"));
            var credential = provider.CreateProtector("Lorekeeper.ExternalAccess.Credentials.v1").Unprotect(connection.ProtectedCredential);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            if (arguments[0] == "upload")
            {
                var info = new FileInfo(arguments[3]);
                if (info.Length > maximumBytes) throw new InvalidDataException();
                await using var input = info.OpenRead();
                using var content = new StreamContent(input);
                using var response = await client.PutAsync(resource, content);
                response.EnsureSuccessStatusCode();
                Console.WriteLine(await response.Content.ReadAsStringAsync());
            }
            else
            {
                using var response = await client.GetAsync(resource, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
                await using var input = await response.Content.ReadAsStreamAsync();
                var destination = Path.GetFullPath(arguments[3]);
                if (File.Exists(destination)) throw new IOException("Choose a new destination.");
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".transfer";
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long length = 0;
                int count;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while ((count = await input.ReadAsync(buffer)) > 0)
                    {
                        length += count;
                        if (length > maximumBytes) throw new InvalidDataException();
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count));
                    }
                }
                File.Move(temporary, destination, overwrite: false);
                Console.WriteLine(JsonSerializer.Serialize(new { length, sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) }));
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException
            or System.Security.Cryptography.CryptographicException or JsonException or FormatException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine("Transfer did not complete. Check the connection, resource identity, byte limit and local file, then retry. No credential or server path is shown.");
            return 1;
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
}
