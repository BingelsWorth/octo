using System.Text.Json;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// The completion poll reads slskd's downloads endpoint, and the response shape decides
/// whether a finished transfer is ever noticed. The per-user endpoint returns a single
/// {username, directories} object; the all-users endpoint returns an array of them.
/// The poll used to accept only the array, so against the per-user endpoint every
/// completed transfer went unseen and the wait always ran the full per-attempt timer:
/// a 3-second download surfaced as playable only after 60 seconds.
///
/// The object fixture below is the real response captured from slskd 0.26.0.
/// </summary>
public class SoulseekTransferPollTests
{
    private const string RemoteFilename =
        @"music\Daft Punk\1997 - Homework [CD]\01 - Daftendirekt.flac";

    // Shape of GET /api/v0/transfers/downloads/{username}: one user group object.
    private const string PerUserObjectJson = """
        {
          "username": "blixquoy",
          "directories": [
            {
              "directory": "music\\Daft Punk\\1997 - Homework [CD]",
              "fileCount": 1,
              "files": [
                {
                  "filename": "music\\Daft Punk\\1997 - Homework [CD]\\01 - Daftendirekt.flac",
                  "state": "Completed, Succeeded",
                  "size": 17361963
                }
              ]
            }
          ]
        }
        """;

    private static string ArrayWrapped => "[" + PerUserObjectJson + "]";

    private static string? Find(string json, string filename)
    {
        using var doc = JsonDocument.Parse(json);
        return SoulseekClient.FindTransferState(doc.RootElement, filename);
    }

    [Fact]
    public void PerUserObjectResponse_FindsCompletedTransfer()
    {
        var state = Find(PerUserObjectJson, RemoteFilename);
        Assert.Equal("Completed, Succeeded", state);
    }

    [Fact]
    public void AllUsersArrayResponse_FindsCompletedTransfer()
    {
        var state = Find(ArrayWrapped, RemoteFilename);
        Assert.Equal("Completed, Succeeded", state);
    }

    [Fact]
    public void FileNotInResponse_ReturnsNull()
    {
        Assert.Null(Find(PerUserObjectJson, @"music\Other\file.flac"));
        Assert.Null(Find(ArrayWrapped, @"music\Other\file.flac"));
    }

    [Fact]
    public void ErroredState_IsReturnedVerbatim()
    {
        var json = PerUserObjectJson.Replace("Completed, Succeeded", "Completed, Errored");
        Assert.Equal("Completed, Errored", Find(json, RemoteFilename));
    }

    [Fact]
    public void MalformedRoots_ReturnNull()
    {
        Assert.Null(Find("\"just a string\"", RemoteFilename));
        Assert.Null(Find("{\"username\":\"x\"}", RemoteFilename));
        Assert.Null(Find("[{\"directories\":\"not-an-array\"}]", RemoteFilename));
    }
}
