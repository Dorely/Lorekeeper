using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.WireRepro;

/// <summary>
/// Loads real provider configuration rows from the development database so the
/// reproduction uses the exact endpoint, model, reasoning-effort, and
/// output-budget settings that failed in the application.
/// </summary>
public sealed record ProviderRow(
    int Id,
    string Name,
    string EndpointUrl,
    string ModelId,
    string? ReasoningEffort,
    int? MaxOutputTokens,
    string? MaxTokensField,
    string? ApiKey);

public static class DevDb
{
    public const string DefaultDbPath = @"C:\Users\jonth\Source\repos\Lorekeeper\Lorekeeper\lorekeeper.db";

    public static string ResolveDbPath() =>
        Environment.GetEnvironmentVariable("LK_TEST_DB") ?? DefaultDbPath;

    public static bool Exists() => File.Exists(ResolveDbPath());

    public static ProviderRow LoadProvider(Func<ProviderRow, bool> predicate)
    {
        var path = ResolveDbPath();
        if (!File.Exists(path))
            throw new InvalidOperationException($"Development database not found at '{path}'. Set LK_TEST_DB to override.");

        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, p.EndpointUrl, p.ModelId, p.ReasoningEffort, p.MaxOutputTokens, p.MaxTokensField, p.ApiKey
            FROM LlmProviders p
            """;
        using var reader = command.ExecuteReader();

        ProviderRow? match = null;
        while (reader.Read())
        {
            var row = new ProviderRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));

            if (match is null && predicate(row))
                match = row;
        }

        if (match is null)
            throw new InvalidOperationException("No provider row matched the requested predicate.");

        // Child rows share the parent's credentials via CredentialSourceId; fall
        // back to the parent key when the matched row itself has none.
        if (string.IsNullOrEmpty(match.ApiKey))
            match = match with { ApiKey = LoadParentApiKey(connection, match.Id) };

        return match;
    }

    private static string? LoadParentApiKey(SqliteConnection connection, int childId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT parent.ApiKey
            FROM LlmProviders child
            JOIN LlmProviders parent ON parent.Id = child.CredentialSourceId
            WHERE child.Id = @id
            """;
        command.Parameters.AddWithValue("id", childId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Loads the reconstructed real Editor system prompt. The persisted context
    /// snapshot stores only bounded excerpts, so the full prompt is read from
    /// the reconstruction artifact captured from the failing turn (see
    /// %TEMP%\lk-sysprompt.txt). Override with LK_TEST_SYSPROMPT.
    /// </summary>
    public static string LoadLatestEditorSystemPrompt(int maxChars = int.MaxValue)
    {
        var path = Environment.GetEnvironmentVariable("LK_TEST_SYSPROMPT")
            ?? Path.Combine(Path.GetTempPath(), "lk-sysprompt.txt");

        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Reconstructed system prompt not found at '{path}'. Capture it from a failing turn or set LK_TEST_SYSPROMPT.");

        var assembled = File.ReadAllTextAsync(path).GetAwaiter().GetResult();
        return maxChars >= assembled.Length ? assembled : assembled[..maxChars];
    }

    /// <summary>Summarizes the persisted context snapshot items (labels and token estimates) for diagnostics.</summary>
    public static IReadOnlyList<(string Key, string Label, int EstimatedTokens)> LoadContextSnapshotSummary()
    {
        var path = ResolveDbPath();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.ContextSnapshotJson
            FROM EditorMessages m
            WHERE m.ContextSnapshotJson IS NOT NULL AND length(m.ContextSnapshotJson) > 2000
            ORDER BY m.Id DESC
            LIMIT 1
            """;
        var snapshotJson = command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("No EditorMessage context snapshot found in the development database.");

        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var summary = new List<(string, string, int)>();
        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                var key = item.TryGetProperty("Key", out var k) ? k.GetString() ?? "" : "";
                var label = item.TryGetProperty("Label", out var l) ? l.GetString() ?? "" : "";
                var tokens = item.TryGetProperty("EstimatedTokens", out var t) ? t.GetInt32() : 0;
                summary.Add((key, label, tokens));
            }
        }
        return summary;
    }
}
