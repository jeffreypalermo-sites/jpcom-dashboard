using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Dashboard.Tests;

/// <summary>The page's own files (<c>wwwroot</c>): it loads its style sheet and its faces from itself. Until
/// 2026-10-10 <c>index.html</c> loaded Jost and Roboto from Google Fonts, so every visit was a request there.</summary>
public partial class PageFilesTests
{
    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Dashboard", "wwwroot"));

    [Theory]
    [InlineData("index.html")]
    [InlineData("css/app.css")]
    public void ThePageAsksGoogleForNoFont(string file)
    {
        var text = File.ReadAllText(Path.Combine(Root(), file));
        Assert.DoesNotContain("fonts.googleapis.com", text);
        Assert.DoesNotContain("fonts.gstatic.com", text);
    }

    [Fact]
    public void TheStartPageLoadsNoStyleSheetOrScriptFromAnotherSite()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "index.html"));
        var loaded = LoadedFromElsewhere().Matches(text).Select(match => match.Value).ToArray();
        Assert.Empty(loaded);
    }

    [Fact]
    public void EveryFaceTheStyleSheetNamesIsAFileOfThePage()
    {
        var css = File.ReadAllText(Path.Combine(Root(), "css", "app.css"));
        var files = FontUrl().Matches(css).Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(4, files.Length);
        Assert.All(files, file => Assert.True(File.Exists(Path.Combine(Root(), "fonts", file)), $"wwwroot/fonts has no {file}"));
        Assert.True(File.Exists(Path.Combine(Root(), "fonts", "OFL-Jost.txt")));
        Assert.True(File.Exists(Path.Combine(Root(), "fonts", "OFL-Roboto.txt")));
    }

    // A link (a style sheet, a preconnect, a preload) or a script with an address on another site.
    [GeneratedRegex("""<(?:link|script)\b[^>]*\b(?:href|src)="https?://""")]
    private static partial Regex LoadedFromElsewhere();

    [GeneratedRegex("""url\("\.\./fonts/([^"]+)"\)""")]
    private static partial Regex FontUrl();
}
