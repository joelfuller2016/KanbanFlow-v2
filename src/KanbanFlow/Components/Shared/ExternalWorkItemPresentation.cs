using KanbanFlow.Models;
using KanbanFlow.Services;
using System.Globalization;

namespace KanbanFlow.Components.Shared;

/// <summary>
/// Resolves external-work-item controls. Durable rows own issue identity. Legacy issue and PR
/// links use the shared leading-header reader for display only; editable text is not identity.
/// </summary>
internal static class ExternalWorkItemPresentation
{
    /// <summary>
    /// Durable link identity first; the sync block is a DISPLAY-ONLY last resort.
    /// </summary>
    /// <remarks>
    /// An upgraded board has no link rows until its first sync promotes them, so without this
    /// every mirrored task loses its issue chip between deploy and that sync -- the PR chip kept
    /// its sync-block fallback and the issue chip did not. The ordering is the whole safety
    /// argument: any GitHub row prevents legacy fallback, including quarantined or invalid rows.
    /// The fallback never feeds registerLink, PrepareAsync or externalNodeId. Its leading-header
    /// shape separates metadata from quoted prose; a user can edit that header, so its shape
    /// does not authenticate the source.
    /// </remarks>
    internal static GitHubIssueReference? FindActiveGitHubIssue(
        IEnumerable<ExternalWorkItemLink>? links,
        string? description)
    {
        var githubLinks = links?.Where(link => link.Provider == ExternalProvider.GitHub).ToArray();
        // A quarantined or invalid durable row is still authoritative: mutable legacy text must
        // not restore the trusted-looking issue control that the recorded identity rejects.
        return githubLinks is { Length: > 0 }
            ? FindActiveGitHubIssue(githubLinks)
            : FindSyncBlockGitHubIssue(description);
    }

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
    /// Reads a single whole Issue record in the leading header. Ambiguous, quoted or malformed
    /// records produce no control. The normalized destination keeps its query and fragment.
    /// </summary>
    internal static GitHubIssueReference? FindSyncBlockGitHubIssue(string? description)
    {
        var reference = ReadLegacyReference(MirroredDescription.IssueUrl(description), "issues");
        return reference is { } value
            ? new GitHubIssueReference(value.Owner, value.Repository, value.Number, value.Url)
            : null;
    }

    /// <summary>
    /// Reads a legacy PR display link through the same bounded, whole-record parser as issues.
    /// Structured pull-request identity remains separate work in #237.
    /// </summary>
    internal static GitHubPullRequestReference? FindSyncBlockPullRequest(string? description)
    {
        var reference = ReadLegacyReference(MirroredDescription.PullRequestUrl(description), "pull");
        return reference is { } value
            ? new GitHubPullRequestReference(value.Owner, value.Repository, value.Number, value.Url)
            : null;
    }

    private static (string Owner, string Repository, int Number, string Url)? ReadLegacyReference(string? url, string kind)
    {
        var numberText = MirroredDescription.ItemNumberForUrl(url, kind);
        if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;

        // ItemNumberForUrl already checked the exact HTTPS GitHub route. Keep the normalized
        // destination rather than rebuilding it and discarding source query/fragment context.
        var uri = new Uri(url!, UriKind.Absolute);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return IsGitHubOwner(parts[0]) && IsGitHubRepositoryName(parts[1])
            ? (parts[0], parts[1], number, uri.AbsoluteUri)
            : null;
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
