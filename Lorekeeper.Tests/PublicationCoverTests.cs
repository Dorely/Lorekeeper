using Lorekeeper.Publish;

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
