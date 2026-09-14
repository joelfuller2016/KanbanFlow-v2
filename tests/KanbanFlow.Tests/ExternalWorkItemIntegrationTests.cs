using KanbanFlow.Components.Shared;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyControl_UsesTheWholeRecordInsteadOfAMarkerInLabelText(bool pullRequest)
    {
        var key = pullRequest ? "Open PR" : "Issue";
        var kind = pullRequest ? "pull" : "issues";
        var expected = $"https://github.com/acme/widgets/{kind}/42";
        var description = $"<!-- kanbanflow-sync -->\n**State:** OPEN · **Labels:** **{key}:** https://github.com/foreign/other/{kind}/99\n**{key}:** {expected}\n<!-- /kanbanflow-sync -->";

        Assert.Equal(expected, ResolveUrl(pullRequest, description));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyControl_RefusesAQuotedBlockOutsideTheLeadingHeader(bool pullRequest)
    {
        var description = "Human context before the quoted header.\n\n" + Header(pullRequest,
            $"https://github.com/acme/widgets/{(pullRequest ? "pull" : "issues")}/42");

        Assert.Null(ResolveUrl(pullRequest, description));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyControl_RefusesDuplicateRecordKeys(bool pullRequest)
    {
        var key = pullRequest ? "Open PR" : "Issue";
        var kind = pullRequest ? "pull" : "issues";
        var description = $"<!-- kanbanflow-sync -->\n**{key}:** https://github.com/acme/widgets/{kind}/42\n**{key}:** https://github.com/foreign/other/{kind}/99\n<!-- /kanbanflow-sync -->";

        Assert.Null(ResolveUrl(pullRequest, description));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyControl_PreservesTheNormalizedDestinationIncludingQueryAndFragment(bool pullRequest)
    {
        var kind = pullRequest ? "pull" : "issues";
        var description = Header(pullRequest, $"https://github.com/acme/old/../widgets/{kind}/42?view=all#comment-7");

        Assert.Equal($"https://github.com/acme/widgets/{kind}/42?view=all#comment-7", ResolveUrl(pullRequest, description));
    }

    private static string Header(bool pullRequest, string value) =>
        $"<!-- kanbanflow-sync -->\n**{(pullRequest ? "Open PR" : "Issue")}:** {value}\n<!-- /kanbanflow-sync -->";

    [Theory]
    [InlineData(false, "?filter=(open)")]
    [InlineData(true, "?filter=(open)")]
    [InlineData(false, "#section.")]
    [InlineData(true, "#section.")]
    [InlineData(false, "#section:")]
    [InlineData(true, "#section:")]
    [InlineData(false, "?filter=a,")]
    [InlineData(true, "?filter=a,")]
    [InlineData(false, "?filter=a;")]
    [InlineData(true, "?filter=a;")]
    public void LegacyControl_PreservesPunctuationInsideTheQueryOrFragment(bool pullRequest, string suffix)
    {
        var expected = $"https://github.com/acme/widgets/{(pullRequest ? "pull" : "issues")}/42{suffix}";

        Assert.Equal(expected, ResolveUrl(pullRequest, Header(pullRequest, expected)));
    }

    private static string? ResolveUrl(bool pullRequest, string description) => pullRequest
        ? ExternalWorkItemPresentation.FindSyncBlockPullRequest(description)?.Url
        : ExternalWorkItemPresentation.FindActiveGitHubIssue([], description)?.Url;
}
