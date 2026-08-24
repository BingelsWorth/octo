using Octo.Models.Settings;
using Octo.Services.Metadata;

namespace Octo.Tests;

/// <summary>
/// The header this applies is what keeps Deezer from localizing genre names to
/// the server's IP country (issue #24), so the interesting cases are the ones
/// where a user-typed value is messy: blank, padded, garbage, or a whole
/// browser-style list pasted in.
/// </summary>
public class AcceptLanguageHeaderTests
{
    private static HttpClient Applied(string language)
    {
        var client = new HttpClient();
        AcceptLanguageHeader.Apply(client, new MetadataSettings { Language = language });
        return client;
    }

    [Fact]
    public void SetsTheHeaderForASimpleCode()
    {
        var client = Applied("en");

        Assert.Contains(client.DefaultRequestHeaders.AcceptLanguage, v => v.Value == "en");
    }

    [Fact]
    public void TrimsWhitespace()
    {
        var client = Applied("  de  ");

        Assert.Contains(client.DefaultRequestHeaders.AcceptLanguage, v => v.Value == "de");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValueLeavesTheHeaderUnset(string language)
    {
        Assert.Empty(Applied(language).DefaultRequestHeaders.AcceptLanguage);
    }

    [Fact]
    public void GarbageValueLeavesTheHeaderUnset()
    {
        // A value the header parser rejects must degrade to provider-default
        // behavior, never break client construction.
        Assert.Empty(Applied("???").DefaultRequestHeaders.AcceptLanguage);
    }

    [Fact]
    public void AcceptsABrowserStyleList()
    {
        var client = Applied("en-US,en;q=0.9");

        Assert.Contains(client.DefaultRequestHeaders.AcceptLanguage, v => v.Value == "en-US");
        Assert.Contains(client.DefaultRequestHeaders.AcceptLanguage, v => v.Value == "en");
    }

    [Fact]
    public void ReapplyingReplacesInsteadOfStacking()
    {
        var client = new HttpClient();
        AcceptLanguageHeader.Apply(client, new MetadataSettings { Language = "de" });
        AcceptLanguageHeader.Apply(client, new MetadataSettings { Language = "en" });

        var value = Assert.Single(client.DefaultRequestHeaders.AcceptLanguage);
        Assert.Equal("en", value.Value);
    }
}
