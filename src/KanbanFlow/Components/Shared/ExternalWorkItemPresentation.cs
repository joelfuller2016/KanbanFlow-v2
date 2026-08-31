using KanbanFlow.Models;
using System.Text.RegularExpressions;

namespace KanbanFlow.Components.Shared;

/// <summary>
/// Resolves display-only external-work-item metadata. Issue identity comes only from durable link
/// rows; the one legacy exception is a PR chip scoped to the exact sync protocol block until #237
/// gives pull requests their own structured identity.
/// </summary>
internal static class ExternalWorkItemPresentation
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex SyncBlock = new(
        @"<!--\s*kanbanflow-sync\s*-->(.*?)<!--\s*/kanbanflow-sync\s*-->",
        RegexOptions.Compiled | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex PullRequest = new(
        @"\*\*Open PR:\*\*\s*https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+)/pull/([1-9]\d*)(?=\s|$)",
        RegexOptions.Compiled,
        MatchTimeout);

    internal static GitHubIssueReference? FindActiveGitHubIssue(
        IEnumerable<ExternalWorkItemLink>? links)
    {
        if (links is null)
            return null;

        foreach (var link in links
                     .Where(link => link.Provider == ExternalProvider.GitHub
                         && link.State == ExternalLinkState.Active)
                     .OrderBy(link => link.Id))
        {
            // A verified link has both immutable remote identity and a positive issue number.
            // Reconstruct the public URL from structured owner/repository fields rather than
            // trusting an arbitrary persisted href value at the render boundary.
            if (string.IsNullOrWhiteSpace(link.ExternalNodeId)
                || link.ExternalNumber is not > 0
                || !IsGitHubOwner(link.RepositoryOwner)
                || !IsGitHubRepositoryName(link.RepositoryName))
            {
                continue;
            }

            var owner = link.RepositoryOwner;
            var repository = link.RepositoryName;
            var number = link.ExternalNumber.Value;
            var url = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/issues/{number}";

            return new GitHubIssueReference(owner, repository, number, url);
        }

        return null;
    }

    /// <summary>
    /// PR identity remains a legacy sync-block concern until #237 gives it a structured row. The
    /// block boundary is mandatory so hand-authored prose can never become a trusted PR control.
    /// </summary>
    internal static GitHubPullRequestReference? FindSyncBlockPullRequest(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;

        try
        {
            var block = SyncBlock.Match(description);
            if (!block.Success)
                return null;

            var match = PullRequest.Match(block.Groups[1].Value);
            if (!match.Success
                || !int.TryParse(match.Groups[3].Value, out var number)
                || number <= 0)
            {
                return null;
            }

            var owner = match.Groups[1].Value;
            var repository = match.Groups[2].Value;
            if (!IsGitHubOwner(owner) || !IsGitHubRepositoryName(repository))
                return null;

            var url = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pull/{number}";
            return new GitHubPullRequestReference(owner, repository, number, url);
        }
        catch (RegexMatchTimeoutException)
        {
            // Descriptions are user-controlled. A timeout must fail closed without taking down
            // the interactive Blazor render path.
            return null;
        }
    }

    private static bool IsGitHubOwner(string value) =>
        IsBoundedSegment(value, 39, character => char.IsAsciiLetterOrDigit(character) || character == '-')
        && value[0] != '-'
        && value[^1] != '-';

    private static bool IsGitHubRepositoryName(string value) =>
        value is not "." and not ".."
        && IsBoundedSegment(value, 100, character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsBoundedSegment(string? value, int maxLength, Func<char, bool> isAllowed) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && value.All(isAllowed);
}

internal sealed record GitHubIssueReference(
    string RepositoryOwner,
    string RepositoryName,
    int Number,
    string Url);

internal sealed record GitHubPullRequestReference(
    string RepositoryOwner,
    string RepositoryName,
    int Number,
    string Url);
