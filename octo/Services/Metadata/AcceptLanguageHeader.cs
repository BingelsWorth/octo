using Octo.Models.Settings;

namespace Octo.Services.Metadata;

/// <summary>
/// Applies the configured metadata language as an Accept-Language header.
/// An invalid configured value must never break client construction, so a
/// value the header parser rejects is simply not sent.
/// </summary>
public static class AcceptLanguageHeader
{
    public static void Apply(HttpClient client, MetadataSettings settings)
    {
        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        var configured = settings.Language;
        if (string.IsNullOrWhiteSpace(configured)) return;
        // Tolerate a pasted browser-style list ("en-US,en;q=0.9"): add each
        // segment on its own so one bad segment cannot take out the rest.
        foreach (var part in configured.Split(','))
        {
            var lang = part.Trim();
            if (lang.Length > 0)
                client.DefaultRequestHeaders.AcceptLanguage.TryParseAdd(lang);
        }
    }
}
