using Cue.Services;

namespace Cue.Tests;

/// <summary>
/// Covers the release-response parsing half of the updater. The settings page calls the check from an
/// <c>async void</c> handler that catches only <see cref="UpdateException"/>, so every failure mode here
/// must surface as one — anything else would reach the UI thread unhandled and take the app down.
/// </summary>
public class UpdateServiceTests
{
    private static readonly Version Current = new(1, 0, 0);

    [Fact]
    public void Parse_NewerReleaseWithInstaller_OffersUpdate()
    {
        var result = UpdateService.Parse(
            """
            {
              "tag_name": "v1.2.0",
              "assets": [
                { "name": "CueSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "size": 1234 },
                { "name": "CueSetup-win-x64.exe.sha256", "browser_download_url": "https://example.test/setup.exe.sha256" }
              ]
            }
            """,
            Current);

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 2, 0), result.Latest);
        Assert.Equal("https://example.test/setup.exe", result.DownloadUrl);
        Assert.Equal("https://example.test/setup.exe.sha256", result.Sha256Url);
        Assert.Equal(1234, result.Size);
    }

    [Fact]
    public void Parse_NewerReleaseWithoutChecksum_RefusesUnverifiedUpdate()
    {
        var exception = Assert.Throws<UpdateException>(() => UpdateService.Parse(
            """
            {
              "tag_name": "v1.2.0",
              "assets": [
                { "name": "CueSetup-win-x64.exe", "browser_download_url": "https://example.test/setup.exe", "size": 1234 }
              ]
            }
            """,
            Current));

        Assert.Contains("검증", exception.Message);
    }

    [Fact]
    public async Task Download_WithoutChecksum_RefusesBeforeFetchingInstaller()
    {
        var update = new UpdateCheckResult(
            Current,
            new Version(1, 2, 0),
            true,
            "https://example.invalid/setup.exe",
            null,
            1234);

        var exception = await Assert.ThrowsAsync<UpdateException>(() => new UpdateService().DownloadAsync(update));

        Assert.Contains("검증", exception.Message);
    }

    [Fact]
    public void Parse_UnparseableTag_ReportsNoUpdateRatherThanFailing()
    {
        var result = UpdateService.Parse("""{ "tag_name": "nightly" }""", Current);

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Latest);
    }

    /// <summary>A captive portal or an intercepting proxy answers 200 with its own HTML body, so a
    /// success status is no guarantee that the body is JSON at all.</summary>
    [Theory]
    [InlineData("<html><body>Sign in to continue</body></html>")]
    [InlineData("")]
    [InlineData("{ \"tag_name\": \"v1.2.0\"")] // truncated response
    public void Parse_MalformedBody_ThrowsUpdateException(string body)
    {
        var exception = Assert.Throws<UpdateException>(() => UpdateService.Parse(body, Current));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    /// <summary>Well-formed JSON of the wrong shape: every property read would throw
    /// <see cref="InvalidOperationException"/> on a non-object root.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"latest\"")]
    [InlineData("null")]
    public void Parse_JsonThatIsNotAnObject_ThrowsUpdateException(string body)
        => Assert.Throws<UpdateException>(() => UpdateService.Parse(body, Current));

    /// <summary>Right shape, wrong value types — GitHub would not send this, but a proxy rewriting the
    /// body could, and <c>GetString()</c> on a number throws.</summary>
    [Fact]
    public void Parse_PropertiesOfTheWrongType_ThrowUpdateException()
        => Assert.Throws<UpdateException>(() => UpdateService.Parse("""{ "tag_name": 120 }""", Current));

    [Fact]
    public void Parse_ReleaseWithoutInstallerAsset_ReportsNoUpdate()
    {
        var result = UpdateService.Parse(
            """
            { "tag_name": "v9.9.9", "assets": [ { "name": "notes.txt", "browser_download_url": "https://example.test/notes.txt" } ] }
            """,
            Current);

        Assert.False(result.UpdateAvailable);
        Assert.Equal(new Version(9, 9, 9), result.Latest);
        Assert.Null(result.DownloadUrl);
    }
}
