namespace Octo.Models.Settings;

public class MetadataSettings
{
    /// <summary>
    /// Language code sent as Accept-Language to the external metadata APIs
    /// (Deezer, Last.fm). Deezer localizes album genre names by caller IP
    /// unless told otherwise, so a server hosted in a non-English country
    /// writes localized genre tags into downloaded files. Empty lets the
    /// provider decide from the server's IP.
    /// </summary>
    public string Language { get; set; } = "en";
}
