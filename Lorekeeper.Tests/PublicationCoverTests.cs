using Lorekeeper.Publish;
using System.Diagnostics;

namespace Lorekeeper.Tests;

public sealed class PublicationCoverTests
{
    [Theory]
    [InlineData("978-0-306-40615-7")]
    [InlineData("9780306406157")]
    public void ValidIsbn13Passes(string isbn) =>
        Assert.True(PublicationIsbn.IsValidIsbn13(isbn));

    [Theory]
    [InlineData("")]
    [InlineData("9780306406158")]
    [InlineData("0306406152")]
    public void InvalidIsbn13Fails(string isbn) =>
        Assert.False(PublicationIsbn.IsValidIsbn13(isbn));
}

internal sealed class TestPublicationPressRuntime : IPublicationPressRuntime
{
    public PublicationPressRuntimeReadiness GetReadiness() => new(true, "Ready");

    public PublicationPressDescription GetDescription() => new(
        4,
        "2.0.0",
        [
            "generic-paperback-v1",
            "kdp-paperback-v1",
            "ingram-paperback-pdfx1a-v1",
            "generic-digital-pdf-v1",
        ],
        default,
        default);

    public ProcessStartInfo CreateStartInfo(Guid jobId, string jobRoot) =>
        throw new NotSupportedException();
}
