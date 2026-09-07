using KanbanFlow.Components.Shared;
using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// Component-model tests for the GitHub badges. The repository intentionally has no bUnit, so the
/// side-effect-free resolver is the established way to prove display decisions without faking a
/// Blazor renderer (see IdeaBadgeTests).
/// </summary>
public sealed class ExternalWorkItemPresentationTests
{
    [Fact]
    public void ActiveVerifiedLink_ProducesCanonicalGitHubIssueReference()
    {
        var link = Link();
        link.Url = "javascript:alert(1)";

        var issue = ExternalWorkItemPresentation.FindActiveGitHubIssue([link]);

        Assert.NotNull(issue);
        Assert.Equal(321, issue.Number);
        Assert.Equal("https://github.com/joelfuller2016/KanbanFlow-v2/issues/321", issue.Url);
    }

    [Theory]
    [InlineData(ExternalLinkState.Quarantined, "I_kwDO_verified")]
    [InlineData(ExternalLinkState.Active, null)]
    public void UnverifiedOrQuarantinedLink_DoesNotProduceTrustedIssueControl(
        ExternalLinkState state,
        string? nodeId)
    {
        var link = Link();
        link.State = state;
        link.ExternalNodeId = nodeId;

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue([link]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveExternalNumber_DoesNotProduceTrustedIssueControl(int? externalNumber)
    {
        var link = Link();
        link.ExternalNumber = externalNumber;

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue([link]));
    }

    [Theory]
    [InlineData("-owner")]
    [InlineData("owner-")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("bad_owner")]
    public void InvalidGitHubOwner_DoesNotProduceTrustedIssueControl(string repositoryOwner)
    {
        var link = Link();
        link.RepositoryOwner = repositoryOwner;

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue([link]));
    }

    [Fact]
    public void MultipleActiveLinks_SelectsLowestLinkId()
    {
        var lowestId = Link();
        lowestId.Id = 3;
        lowestId.ExternalNumber = 123;

        var highestId = Link();
        highestId.Id = 8;
        highestId.ExternalNumber = 456;

        var issue = ExternalWorkItemPresentation.FindActiveGitHubIssue([highestId, lowestId]);

        Assert.NotNull(issue);
        Assert.Equal(123, issue.Number);
        Assert.Equal("https://github.com/joelfuller2016/KanbanFlow-v2/issues/123", issue.Url);
    }

    [Fact]
    public void MutableTaskText_CannotCreateIssueIdentity()
    {
        var unverifiedLink = Link();
        unverifiedLink.ExternalNodeId = null;
        unverifiedLink.ExternalNumber = 336;
        unverifiedLink.Url = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/336";

        var task = new TaskItem
        {
            Name = "#336 looks linked",
            Description = """
                **Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/336
                **Open PR:** https://github.com/joelfuller2016/KanbanFlow-v2/pull/506
                """,
            ExternalLinks = [unverifiedLink]
        };

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue(task.ExternalLinks));
        Assert.Null(ExternalWorkItemPresentation.FindSyncBlockPullRequest(task.Description));
        // The display-only fallback does not weaken this: the URLs above sit in free prose, not
        // in the machine-written sync block, so the block boundary still refuses them.
        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue(task.ExternalLinks, task.Description));
    }

    [Fact]
    public void SyncBlockIssue_RendersTheChipWhileTheBoardHasNoDurableLinkYet()
    {
        // The upgrade gap: an existing board carries the pre-#336 sync block and NO link rows
        // until its first sync promotes them. Without the fallback every mirrored task silently
        // loses its issue chip in that window.
        const string description = """
            <!-- kanbanflow-sync -->
            **Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/336
            <!-- /kanbanflow-sync -->
            """;

        var issue = ExternalWorkItemPresentation.FindActiveGitHubIssue(null, description);

        Assert.NotNull(issue);
        Assert.Equal(336, issue.Number);
        Assert.Equal("joelfuller2016", issue.RepositoryOwner);
        Assert.Equal("KanbanFlow-v2", issue.RepositoryName);
        Assert.Equal("https://github.com/joelfuller2016/KanbanFlow-v2/issues/336", issue.Url);
    }

    [Fact]
    public void DurableLink_AlwaysWinsOverTheSyncBlockFallback()
    {
        // The ordering IS the safety argument. A stale sync block must never override the row
        // that actually carries immutable identity, so the fallback can only ever ADD a chip to a
        // task that would otherwise show none.
        const string staleBlock = """
            <!-- kanbanflow-sync -->
            **Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/999
            <!-- /kanbanflow-sync -->
            """;

        var issue = ExternalWorkItemPresentation.FindActiveGitHubIssue([Link()], staleBlock);

        Assert.NotNull(issue);
        Assert.Equal(321, issue.Number);
    }

    [Fact]
    public void IssueUrlOutsideTheSyncBlock_DoesNotProduceAChip()
    {
        // Hand-authored prose is not provenance. Only the machine-written block is read.
        const string handAuthored =
            "See **Issue:** https://github.com/attacker/spoof/issues/1 for context";

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue(null, handAuthored));
        Assert.Null(ExternalWorkItemPresentation.FindSyncBlockGitHubIssue(handAuthored));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad owner/repo")]
    public void InvalidRepositoryInTheSyncBlock_DoesNotProduceAChip(string repositoryName)
    {
        var description = $"""
            <!-- kanbanflow-sync -->
            **Issue:** https://github.com/joelfuller2016/{repositoryName}/issues/336
            <!-- /kanbanflow-sync -->
            """;

        Assert.Null(ExternalWorkItemPresentation.FindSyncBlockGitHubIssue(description));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DotPathRepositoryName_DoesNotProduceTrustedIssueControl(string repositoryName)
    {
        var link = Link();
        link.RepositoryName = repositoryName;

        Assert.Null(ExternalWorkItemPresentation.FindActiveGitHubIssue([link]));
    }

    [Fact]
    public void PullRequestOutsideSyncBlock_DoesNotProduceTrustedPrControl()
    {
        const string handAuthored =
            "Please review **Open PR:** https://github.com/joelfuller2016/KanbanFlow-v2/pull/487";

        Assert.Null(ExternalWorkItemPresentation.FindSyncBlockPullRequest(handAuthored));
    }

    [Fact]
    public void PullRequestResolver_OnlyReadsTheMarkedSyncBlock()
    {
        const string description = """
            <!-- kanbanflow-sync -->
            **Issue:** https://github.com/joelfuller2016/KanbanFlow-v2/issues/336
            **Open PR:** https://github.com/joelfuller2016/KanbanFlow-v2/pull/487
            <!-- /kanbanflow-sync -->

            **Open PR:** https://github.com/attacker/spoof/pull/999
            """;

        var pullRequest = ExternalWorkItemPresentation.FindSyncBlockPullRequest(description);

        Assert.NotNull(pullRequest);
        Assert.Equal(487, pullRequest.Number);
        Assert.Equal("https://github.com/joelfuller2016/KanbanFlow-v2/pull/487", pullRequest.Url);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DotPathRepositoryName_DoesNotProduceTrustedPrControl(string repositoryName)
    {
        var description = $"""
            <!-- kanbanflow-sync -->
            **Open PR:** https://github.com/joelfuller2016/{repositoryName}/pull/487
            <!-- /kanbanflow-sync -->
            """;

        Assert.Null(ExternalWorkItemPresentation.FindSyncBlockPullRequest(description));
    }

    private static ExternalWorkItemLink Link() => new()
    {
        Id = 4,
        TaskId = 9,
        Provider = ExternalProvider.GitHub,
        State = ExternalLinkState.Active,
        RepositoryExternalId = "R_kwDO_repo",
        RepositoryOwner = "joelfuller2016",
        RepositoryName = "KanbanFlow-v2",
        ExternalNodeId = "I_kwDO_verified",
        ExternalNumber = 321,
        Url = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/321"
    };
}
