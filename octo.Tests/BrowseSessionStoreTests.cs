using Octo.Services.Admin;

namespace Octo.Tests;

/// <summary>
/// This token is the only thing standing between the browse endpoint and anyone who
/// can reach Octo's port, so the interesting cases are the ones where a naive store
/// says yes: an empty token, an unknown token, or one that should have lapsed.
/// </summary>
public class BrowseSessionStoreTests
{
    [Fact]
    public void AMintedTokenValidates()
    {
        var store = new BrowseSessionStore();

        Assert.True(store.Validate(store.Create("winters")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void AnythingNotMintedByUsIsRejected(string? token)
    {
        var store = new BrowseSessionStore();
        store.Create("winters"); // a live session must not make other tokens valid

        Assert.False(store.Validate(token));
    }

    [Fact]
    public void TokensAreUnpredictableAndNotSharedBetweenSessions()
    {
        var store = new BrowseSessionStore();

        var first = store.Create("winters");
        var second = store.Create("winters");

        Assert.NotEqual(first, second);
        // 32 bytes hex. Guessing is not meant to be on the table.
        Assert.Equal(64, first.Length);
    }
}
