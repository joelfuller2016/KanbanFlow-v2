using System.Net;
using System.Text.RegularExpressions;
using KanbanFlow.Components.Shared;
using KanbanFlow.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanFlow.Tests;

public sealed class KanbanCardRenderedTitleTests
{
    private const string IssueUrl = "https://github.com/owner/repo/issues/612";

    [Theory]
    [InlineData("#612 Read the whole title", "Read the whole title")]
    [InlineData("#612 P1: Keep names & links", "Keep names & links")]
    [InlineData("#612 p2 — Wrap a long title", "Wrap a long title")]
    [InlineData("#612 [P2][bug] Render the authored category", "[bug] Render the authored category")]
    [InlineData("#612 P2P transfers", "P2P transfers")]
    [InlineData("#612 [P2]", "[P2]")]
    public async Task VerifiedIssueMovesItsPrefixOutOfTheVisibleTitle(string authored, string visible)
    {
        var task = CreateTask(authored, VerifiedLink(612));

        var html = await RenderCard(task);

        AssertVisibleTitle(html, visible);
        Assert.Contains($"title=\"{authored}\"", html);
        Assert.Contains($"aria-label=\"Open task details for {authored}\"", html);
        Assert.Contains($"href=\"{IssueUrl}\"", html);
        Assert.Contains("data-testid=\"board-card-41-github-link\"", html);
        Assert.Equal(authored, task.Name);
    }

    [Theory]
    [InlineData("#6123 P1: Match the entire issue number", 612, true)]
    [InlineData("#612 P1: Honor the linked identity", 613, true)]
    [InlineData("P1: Authored title without an issue prefix", 612, true)]
    [InlineData("#612 P1: Quarantined link", 612, false)]
    public async Task ATitleWithoutAMatchingVerifiedPrefixIsShownInFull(
        string authored, int issueNumber, bool verified)
    {
        var link = VerifiedLink(issueNumber);
        if (!verified)
            link.State = ExternalLinkState.Quarantined;

        var html = await RenderCard(CreateTask(authored, link));

        AssertVisibleTitle(html, authored);
    }

    [Fact]
    public async Task SeededLegacyIssueMovesItsPrefixOutOfTheVisibleTitle()
    {
        const string visible = "KanbanFlow is fully built and never connected — wire the MCP server, promote the skills, and prove adoption with a two-week kill criterion";
        const string authored = "#477 P2: " + visible;
        const string url = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/477";
        var task = CreateTask(authored);
        task.Description = LegacyDescription(url);

        var html = await RenderCard(task);

        AssertVisibleTitle(html, visible);
        Assert.Contains($"title=\"{authored}\"", html);
        Assert.Contains($"aria-label=\"Open task details for {authored}\"", html);
        Assert.Contains($"href=\"{url}\"", html);
        Assert.Contains("data-testid=\"board-card-41-github-link\"", html);
        Assert.Contains("Keep the full description in the task.", html);
        Assert.Equal(authored, task.Name);
        Assert.Equal(LegacyDescription(url), task.Description);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/issues/613", true)]
    [InlineData("https://github.com/owner/repo/pull/612", false)]
    [InlineData(null, false)]
    public async Task LegacyIssueMustRenderAMatchingLinkToShortenTheTitle(string? url, bool hasLink)
    {
        const string authored = "#612 P1: Preserve the authored prefix";
        var task = CreateTask(authored);
        task.Description = url is null ? null : LegacyDescription(url);

        var html = await RenderCard(task);

        AssertVisibleTitle(html, authored);
        Assert.Equal(hasLink, html.Contains("data-testid=\"board-card-41-github-link\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExternalLinkState.Active, true)]
    [InlineData(ExternalLinkState.Quarantined, false)]
    public async Task DurableIssueRowOverridesAMatchingLegacyIssueNumber(ExternalLinkState state, bool hasLink)
    {
        const string authored = "#612 P1: Preserve the authored prefix";
        var link = VerifiedLink(613);
        link.State = state;
        var task = CreateTask(authored, link);
        task.Description = LegacyDescription(IssueUrl);

        var html = await RenderCard(task);

        AssertVisibleTitle(html, authored);
        Assert.DoesNotContain($"href=\"{IssueUrl}\"", html);
        Assert.Equal(hasLink, html.Contains("data-testid=\"board-card-41-github-link\"", StringComparison.Ordinal));
    }

    private static string LegacyDescription(string issueUrl) => $"""
            <!-- kanbanflow-sync -->
            **Issue:** {issueUrl}
            **State:** OPEN · **Milestone:** none · **Labels:** documentation, enhancement, ai-agent, deploy
            **Open PR:** none
            **Synced:** 2026-08-30T14:04:07.474Z
            <!-- /kanbanflow-sync -->

            Keep the full description in the task.
            """;

    private static void AssertVisibleTitle(string html, string visible)
    {
        var title = Regex.Match(html, "<span class=\"board-card-title-text\"[^>]*>(?<title>[^<]*)</span>");
        Assert.True(title.Success, "The title button should have a visible text span for clamping.");
        Assert.Equal(visible, title.Groups["title"].Value);
    }

    private static async Task<string> RenderCard(TaskItem task)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KanbanCard.Task)] = task
            });
            var rendered = await renderer.RenderComponentAsync<KanbanCard>(parameters);
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
    }

    private static TaskItem CreateTask(string name, ExternalWorkItemLink? link = null) => new()
    {
        Id = 41,
        Name = name,
        Feature = new Feature { Name = "Board" },
        ExternalLinks = link is null ? [] : [link]
    };

    private static ExternalWorkItemLink VerifiedLink(int number) => new()
    {
        Provider = ExternalProvider.GitHub,
        RepositoryExternalId = "R_board",
        RepositoryOwner = "owner",
        RepositoryName = "repo",
        ExternalNodeId = "I_verified",
        ExternalNumber = number
    };
}
