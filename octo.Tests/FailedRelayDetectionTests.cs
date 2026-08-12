using System.Text;
using Octo.Controllers;

namespace Octo.Tests;

/// <summary>
/// Subsonic reports its own errors inside an HTTP 200, so a rejected login and an empty
/// library look identical to a status-code check. That mattered little while search
/// returned whatever the library gave it, but the discovery top-up reads "no local
/// matches" as an invitation to fill the page, which would dress a broken connection up
/// as a healthy search.
/// </summary>
public class FailedRelayDetectionTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void JsonErrorEnvelopeIsDetected()
    {
        var body = B(@"{""subsonic-response"":{""status"":""failed"",""version"":""1.16.1"",
                       ""error"":{""code"":40,""message"":""Wrong username or password""}}}");

        Assert.True(SubsonicController.IsFailedSubsonicBody(body, "application/json"));
    }

    [Fact]
    public void XmlErrorEnvelopeIsDetected()
    {
        var body = B(@"<?xml version=""1.0"" encoding=""UTF-8""?>
            <subsonic-response xmlns=""http://subsonic.org/restapi"" status=""failed"" version=""1.16.1"">
              <error code=""40"" message=""Wrong username or password""/>
            </subsonic-response>");

        Assert.True(SubsonicController.IsFailedSubsonicBody(body, "application/xml"));
    }

    [Fact]
    public void AnEmptyButSuccessfulResultIsNotAFailure()
    {
        // The case that must keep working: a real search that genuinely matched nothing.
        var json = B(@"{""subsonic-response"":{""status"":""ok"",""version"":""1.16.1"",""searchResult3"":{}}}");
        var xml = B(@"<subsonic-response xmlns=""http://subsonic.org/restapi"" status=""ok"" version=""1.16.1"">
                        <searchResult3/></subsonic-response>");

        Assert.False(SubsonicController.IsFailedSubsonicBody(json, "application/json"));
        Assert.False(SubsonicController.IsFailedSubsonicBody(xml, "application/xml"));
    }

    [Fact]
    public void UnreadableOrEmptyBodiesAreNotTreatedAsFailures()
    {
        // Unparseable is not the same as failed; the normal path should still handle it.
        Assert.False(SubsonicController.IsFailedSubsonicBody(null, "application/json"));
        Assert.False(SubsonicController.IsFailedSubsonicBody(Array.Empty<byte>(), "application/json"));
        Assert.False(SubsonicController.IsFailedSubsonicBody(B("not json at all"), "application/json"));
        Assert.False(SubsonicController.IsFailedSubsonicBody(B("<broken"), "application/xml"));
    }
}
