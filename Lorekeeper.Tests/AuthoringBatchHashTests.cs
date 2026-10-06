using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Tests;

public sealed class AuthoringBatchHashTests
{
    // Fixtures are batches hashed by the editor's canonicalJson; the server must accept them or edits never persist.
    // The footnote fixture omits model defaults such as "decorative", so it only matches when hashed as sent.
    [Theory]
    [InlineData("editor-ascii-punctuation.json")]
    [InlineData("editor-unicode-and-controls.json")]
    [InlineData("editor-footnote-reference.json")]
    public void Server_hash_matches_editor_hash(string fixture)
    {
        using var request = JsonDocument.Parse(File.ReadAllText(FindFixture(fixture)));
        var batch = request.RootElement.Deserialize<AuthoringBatchV1>(ManuscriptCodec.JsonOptions)!;

        Assert.Equal(batch.RequestHash, AuthoringBatchHash.Compute(request.RootElement));
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
