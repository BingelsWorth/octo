using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Which peer file gets accepted decides what audio ends up in the library, and a wrong
/// choice is invisible: the tagger stamps the correct title, track number and cover art
/// onto whatever arrived, so the library looks right and plays wrong.
///
/// Every filename below is real, taken from a live Soulseek walk of Massive Attack's
/// Mezzanine that silently pulled four tracks off a Mad Professor remix album.
/// </summary>
public class SoulseekCandidateMatchingTests
{
    // ---- filename matching ----------------------------------------------------

    /// <summary>
    /// A folder name must not satisfy a track title. Matching used to run against the
    /// whole path, so any file inside a "Mezzanine (1998)" directory answered a search
    /// for the track "Mezzanine".
    /// </summary>
    [Fact]
    public void FolderNameDoesNotSatisfyATrackTitle()
    {
        const string inMezzanineFolder = @"music\Massive Attack\Mezzanine (1998)\03 - Teardrop.flac";

        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(inMezzanineFolder, "Mezzanine"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(inMezzanineFolder, "Teardrop"));
    }

    /// <summary>
    /// The filename check alone CANNOT catch every case, and pretending otherwise would
    /// be the wrong lesson. This peer encodes artist, album and title into one flat
    /// filename, so the album name "Mezzanine" sits in the leaf and the name check passes
    /// a file that is actually a different song. Duration and the variant marker are what
    /// reject it, which is why all three layers exist.
    /// </summary>
    [Fact]
    public void FlatFilenamesNeedTheOtherTwoSignals()
    {
        const string wrong =
            @"Massive Attack_Massive Attack V Mad Professor Part II (Mezzanine Remix Tapes '98)_08_Group Four (Security Forces Dub).flac";

        // The name check cannot tell: "mezzanine" really is in the leaf.
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(wrong, "Mezzanine"));

        // These do. "Mezzanine" is 354s and this file is 494s.
        Assert.False(SoulseekDownloadService.DurationPlausible(494, 354));
        Assert.True(SoulseekDownloadService.VariantPenalty(wrong, "Mezzanine") > 0);
    }

    [Fact]
    public void RealTrackFilenamesStillMatch()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"music\Massive Attack\Mezzanine (1998)\09 - Mezzanine.flac", "Mezzanine"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"Massive Attack_Collected_01-05_Inertia Creeps.flac", "Inertia Creeps"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"Massive-Attack-Mezzanine-06-Dissolved-Girl.flac", "Dissolved Girl"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"07 Massive Attack - Man Next Door.flac", "Man Next Door"));
    }

    /// <summary>Any-one-token matching let "Group Four" be satisfied by unrelated files.</summary>
    [Fact]
    public void EverySignificantTokenMustBePresent()
    {
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Frankie Valli - The Four Seasons.flac", "Group Four"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "10 - Group Four.flac", "Group Four"));
    }

    /// <summary>Short titles must not be filtered away by the >=3 char token rule.</summary>
    [Fact]
    public void ShortTitlesAreNotOverFiltered()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Kendrick Lamar - DNA..flac", "DNA."));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "anything at all.flac", "M.I.A."));
    }

    // ---- duration -------------------------------------------------------------

    [Fact]
    public void WildlyWrongLengthIsRejected()
    {
        // "Mezzanine" is 354s; the file that arrived was 494s.
        Assert.False(SoulseekDownloadService.DurationPlausible(494, 354));
        // "Exchange" is 251s; a dub mix of 344s arrived.
        Assert.False(SoulseekDownloadService.DurationPlausible(344, 251));
    }

    [Fact]
    public void MasteringDriftIsAccepted()
    {
        // Real spread measured across the album's correctly-matched tracks.
        Assert.True(SoulseekDownloadService.DurationPlausible(380, 378));
        Assert.True(SoulseekDownloadService.DurationPlausible(331, 327));
        Assert.True(SoulseekDownloadService.DurationPlausible(299, 299));
    }

    /// <summary>
    /// The near misses that a looser tolerance let through: an "(Angel Dust)" dub 12s off
    /// and an "(Floating on Dubwise)" dub 11s off, against correct tracks that never
    /// drifted past 4s.
    /// </summary>
    [Fact]
    public void NearMissDubMixesAreRejected()
    {
        Assert.False(SoulseekDownloadService.DurationPlausible(366, 378));
        Assert.False(SoulseekDownloadService.DurationPlausible(367, 356));
    }

    /// <summary>
    /// Neither of these contains a keyword a marker list would catch. What gives them away
    /// is that they are bracketed additions the requested title never asked for.
    /// </summary>
    [Fact]
    public void UnrequestedBracketedAdditionsArePenalised()
    {
        Assert.True(SoulseekDownloadService.VariantPenalty(
            "2-02 Massive Attack & Mad Professor - Angel (Angel Dust).flac", "Angel") > 0);
        Assert.True(SoulseekDownloadService.VariantPenalty(
            "Massive Attack - Mezzanine - 04 - Inertia Creeps (Floating on Dubwise).flac",
            "Inertia Creeps") > 0);
    }

    /// <summary>Year and format tags are how peers label a good rip, not a different take.</summary>
    [Fact]
    public void YearAndFormatTagsAreNotTreatedAsVariants()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty("Angel (1998).flac", "Angel"));
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty("Angel [FLAC].flac", "Angel"));
    }

    [Fact]
    public void UnknownLengthIsNotTreatedAsEvidence()
    {
        Assert.True(SoulseekDownloadService.DurationPlausible(null, 354));
        Assert.True(SoulseekDownloadService.DurationPlausible(354, null));
        Assert.True(SoulseekDownloadService.DurationPlausible(0, 354));
    }

    // ---- variant markers ------------------------------------------------------

    /// <summary>
    /// The case duration cannot catch: "Group Four (Security Forces dub)" runs 495s
    /// against the album version's 493s, so only the name gives it away.
    /// </summary>
    [Fact]
    public void UnrequestedVariantsSortBelowPlainMatches()
    {
        var dub = SoulseekDownloadService.VariantPenalty(
            "2-08 Massive Attack & Mad Professor - Group Four (Security Forces dub).flac", "Group Four");
        var plain = SoulseekDownloadService.VariantPenalty(
            "10 Massive Attack - Group Four.flac", "Group Four");

        Assert.True(dub > plain, "a dub mix must rank below the plain album version");
        Assert.Equal(0, plain);
    }

    /// <summary>A remix that was actually asked for must not be penalised.</summary>
    [Fact]
    public void RequestedVariantsAreNotPenalised()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty(
            "05 - Teardrop (Mazaruni Dub One).flac", "Teardrop (Mazaruni Dub One)"));
    }

    /// <summary>Word boundaries, so "Oliver" or "delivery" is not read as "live".</summary>
    [Fact]
    public void MarkersMatchWholeWordsOnly()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty(
            "Oliver Nelson - Stolen Moments.flac", "Stolen Moments"));
    }
}
