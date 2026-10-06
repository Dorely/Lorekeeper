using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Tests;

public sealed class AuthoringBatchHashTests
{
    // Fixtures are batches hashed by the editor's canonicalJson; the server must accept them or edits never persist.
    [Theory]
    [InlineData("editor-ascii-punctuation.json")]
    [InlineData("editor-unicode-and-controls.json")]
    public void Server_hash_matches_editor_hash(string fixture)
    {
        var batch = JsonSerializer.Deserialize<AuthoringBatchV1>(File.ReadAllText(FindFixture(fixture)), ManuscriptCodec.JsonOptions)!;

        Assert.Equal(batch.RequestHash, AuthoringBatchHash.Compute(batch));
    }

    private static string FindFixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Lorekeeper.Tests", "Fixtures", "AuthoringBatches", name);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException("The authoring batch fixture was not found.", name);
    }
}
