using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.LastFm;

namespace Octo.Tests;

/// <summary>
/// "Can Last.fm answer at all" and "is the radio feature switched on" are different
/// questions. They used to be one property, so turning radio off also emptied the search
/// bar of discovery results — a setting doing something its name does not say.
/// </summary>
public class LastFmServiceTests
{
    private static LastFmService With(string apiKey, bool enableRadio, string language = "en") =>
        new(new HttpClient(),
            Options.Create(new LastFmSettings { ApiKey = apiKey, EnableRadio = enableRadio }),
            Options.Create(new MetadataSettings { Language = language }),
            new Mock<ILogger<LastFmService>>().Object);

    [Theory]
    [InlineData("", true, false)]
    [InlineData("", false, false)]
    [InlineData("abc123", true, true)]
    [InlineData("abc123", false, true)]
    public void HasApiKey_DependsOnlyOnTheKey(string key, bool radio, bool expected)
    {
        // Search discovery gates on this, so EnableRadio must not appear in it.
        Assert.Equal(expected, With(key, radio).HasApiKey);
    }

    [Theory]
    [InlineData("abc123", true, true)]
    [InlineData("abc123", false, false)]
    [InlineData("", true, false)]
    public void IsRadioEnabled_NeedsBothTheKeyAndTheSwitch(string key, bool radio, bool expected)
    {
        Assert.Equal(expected, With(key, radio).IsRadioEnabled);
    }

    [Fact]
    public void RadioOffStillLeavesSearchDiscoveryAvailable()
    {
        // The regression this pair exists to prevent.
        var svc = With("abc123", enableRadio: false);

        Assert.True(svc.HasApiKey);
        Assert.False(svc.IsRadioEnabled);
    }

    [Fact]
    public void Construction_AppliesMetadataLanguageToTheClient()
    {
        var client = new HttpClient();
        _ = new LastFmService(client,
            Options.Create(new LastFmSettings { ApiKey = "abc123" }),
            Options.Create(new MetadataSettings { Language = "en" }),
            new Mock<ILogger<LastFmService>>().Object);

        Assert.Contains(client.DefaultRequestHeaders.AcceptLanguage, v => v.Value == "en");
    }
}
