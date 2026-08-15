using AngleSharp.Html.Parser;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// The task description render path (#357).
///
/// Until #357 <c>TaskItem.Description</c> was the only markdown-bearing field in the app that
/// never rendered: both surfaces bound it straight into a <c>&lt;textarea&gt;</c>. Routing it
/// through <see cref="MarkdownRenderer"/> is what makes it readable — and it is also the first
/// time THIRD-PARTY-AUTHORED bytes reach the renderer, because <c>scripts/portfolio-sync.mjs</c>
/// builds a mirrored task's description out of a GitHub issue body. Anyone who can open an issue
/// on a synced repo now controls bytes that become live DOM. Those bytes were previously inert
/// for one accidental reason only: they never left a textarea.
///
/// So these tests cover the two properties that change with #357, against the REAL renderer and
/// the REAL service — no stubs:
///   1. RENDER boundary — the <c>kanbanflow-sync</c> markers are invisible, the metadata inside
///      them still renders, and hostile issue bodies stay inert.
///   2. STORAGE boundary — the marker bytes survive an edit-and-save round trip. #336 states the
///      rule these two halves come from: sync markers are protocol, not content; strip them at
///      the render boundary, NEVER at storage, because they are the durable repo↔board link.
///
/// Every assertion here is paired with a non-vacuity assertion (benign markdown in the SAME
/// input must still render), because "the payload is absent" is also true of a renderer that
/// returns nothing at all.
/// </summary>
public sealed class TaskDescriptionRenderingTests
{
    private static readonly MarkdownRenderer Renderer = new();

    private const string IssueUrl = "https://github.com/joelfuller2016/ZeroRouter/issues/72";
    private const string LayoutEscapeStyle =
        "position:fixed;inset:0;top:0;right:0;bottom:0;left:0;" +
        "width:100vw;height:100vh;min-width:100vw;max-width:none;min-height:100vh;max-height:none;" +
        "z-index:999999;transform:scale(2);display:block;overflow:visible;pointer-events:auto;" +
        "background-color:red;color:white;font-weight:bold";

    /// <summary>
    /// Template-faithful fixture: the exact line sequence <c>descriptionFor()</c> emits at
    /// <c>scripts/portfolio-sync.mjs:456-473</c> (verified byte-for-byte against the live
    /// instance's <c>GET /api/tasks/351</c> on 2026-08-02). Hand-written approximations are how a
    /// green suite ends up testing a shape the producer never emits, so this mirrors the producer's
    /// own <c>lines.join("\n")</c> rather than eyeballing the result.
    /// </summary>
    private static string SyncedDescription(string body) => string.Join("\n",
    [
        "<!-- kanbanflow-sync -->",
        $"**Issue:** {IssueUrl}",
        "**State:** OPEN · **Milestone:** none · **Labels:** enhancement",
        "**Open PR:** none",
        "**Synced:** 2026-07-12T13:19:18.797Z",
        "<!-- /kanbanflow-sync -->",
        "",
        body,
    ]);

    // ── Render boundary: the markers are protocol, and the reader must never see them ───────────

    [Fact]
    public void SyncedDescription_NeverShowsTheSyncMarkersToTheReader()
    {
        var html = Renderer.RenderToSafeHtml(SyncedDescription("## Summary\n\nReal **body** text."));

        // The whole point of #357's acceptance criterion 4. No stripping code exists anywhere in
        // the app — the sanitizer drops comment nodes — so this test IS the proof of that claim.
        // If a dependency bump or an allow-list change ever starts keeping comments, the markers
        // become visible on every mirrored task and this goes red instead of shipping silently.
        Assert.DoesNotContain("kanbanflow-sync", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<!--", html, StringComparison.Ordinal);
        Assert.DoesNotContain("-->", html, StringComparison.Ordinal);

        // Non-vacuity: a renderer that returned "" would satisfy every line above.
        Assert.Contains("<h2>Summary</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>body</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncedDescription_RendersTheBlockMetadataAndLinksTheIssueUrl()
    {
        var html = Renderer.RenderToSafeHtml(SyncedDescription("body"));

        // The markers vanish; what sat BETWEEN them is content and must survive. This is the
        // difference between stripping the markers and stripping the block.
        Assert.Contains("<strong>Issue:</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>State:</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Synced:</strong>", html, StringComparison.Ordinal);

        // AC 4's other half: the issue URL is a working link, not dead text. Markdig's
        // UseAdvancedExtensions autolinks the bare URL — which is also what makes EditItemModal's
        // explicit GitHub chips a convenience rather than the only way to reach the issue.
        Assert.Contains($"<a href=\"{IssueUrl}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderStripsTheMarkers_WhileTheEditableSourceKeepsThem()
    {
        // Render boundary, not storage boundary (#336). The markers must disappear from the HTML
        // the user sees but survive byte-for-byte in the textarea source, which is what lets a
        // synced edit round-trip them. Asserting source equality is not a test — strings are
        // immutable; the meaningful claim is that the two sides disagree in exactly one direction.
        var source = SyncedDescription("body");

        var html = Renderer.RenderToSafeHtml(source);

        Assert.Contains("<!-- kanbanflow-sync -->", source, StringComparison.Ordinal);
        Assert.DoesNotContain("kanbanflow-sync", html, StringComparison.OrdinalIgnoreCase);
        // Non-vacuity: the body between the markers must have survived the render.
        Assert.Contains("<strong>Issue:</strong>", html, StringComparison.Ordinal);
    }

    // ── Render boundary: hostile issue bodies ───────────────────────────────────────────────────

    /// <summary>
    /// The provenance case. A description is a GitHub issue body, so this payload models what an
    /// outside contributor can put on a synced repo — inside the real sync-block wrapper, not as a
    /// bare string, because the wrapper is what every mirrored description actually looks like.
    ///
    /// Asserted on the emitted DOM, not on "nothing visibly happened": re-parse the sanitized HTML
    /// and require zero scriptable elements and zero event-handler attributes. A string-only check
    /// would also pass against a renderer that emitted nothing, which is why the benign
    /// <c>**bold**</c> in the same input must still come out as &lt;strong&gt;.
    /// </summary>
    [Fact]
    public void HostileIssueBody_IsInert_AndBenignMarkdownInTheSameDescriptionStillRenders()
    {
        var description = SyncedDescription(string.Join("\n\n",
            "<img src=x onerror=alert(1)>",
            "<script>alert(1)</script>",
            "[click me](javascript:alert(1))",
            "<a href=\"https://x.example\" onclick=\"steal()\">x</a>",
            "<svg><animate onbegin=alert(1) attributeName=x></svg>",
            "This part is **bold** and must survive."));

        var html = Renderer.RenderToSafeHtml(description);
        var dom = new HtmlParser().ParseDocument("<!doctype html><body>" + html);

        Assert.Empty(dom.QuerySelectorAll("script"));
        Assert.Empty(dom.QuerySelectorAll("svg"));
        Assert.Empty(dom.QuerySelectorAll("iframe"));
        Assert.DoesNotContain(
            dom.All.SelectMany(e => e.Attributes),
            a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            dom.QuerySelectorAll("a"),
            a => (a.GetAttribute("href") ?? string.Empty)
                .StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", html, StringComparison.OrdinalIgnoreCase);

        // Non-vacuity, twice over: the benign markdown in the same description renders, and the
        // sync block above the payload still linked its issue URL.
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{IssueUrl}\">", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# Layout probe {style=\"" + LayoutEscapeStyle + "\"}")]
    [InlineData("<p style=\"" + LayoutEscapeStyle + "\">Layout probe</p>")]
    public void HostileIssueBody_CannotUseInlineCssToEscapeItsPanel(string body)
    {
        var html = Renderer.RenderToSafeHtml(SyncedDescription(body));
        var dom = new HtmlParser().ParseDocument("<!doctype html><body>" + html);
        var probe = dom.QuerySelectorAll("h1, p").Single(e => e.TextContent == "Layout probe");
        var style = probe.GetAttribute("style") ?? string.Empty;
        var properties = style
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(declaration => declaration.Split(':', 2)[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Layout and stacking properties let stored Markdown cover or escape the task panel.
        foreach (var forbidden in new[]
                 {
                     "position", "inset", "top", "right", "bottom", "left",
                     "width", "height", "min-width", "max-width", "min-height", "max-height",
                     "z-index", "transform", "display", "overflow", "pointer-events"
                 })
        {
            Assert.DoesNotContain(forbidden, properties);
        }
        Assert.DoesNotContain("100vw", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("100vh", style, StringComparison.OrdinalIgnoreCase);

        // Non-layout emphasis remains available; this must not pass by stripping all style/content.
        Assert.Contains("background-color", properties);
        Assert.Contains("color", properties);
        Assert.Contains("font-weight", properties);
        Assert.Equal("Layout probe", probe.TextContent);
    }

    // ── Storage boundary: the marker bytes must round-trip ──────────────────────────────────────

    /// <summary>
    /// #336's rule, enforced where it can actually be broken. The block is the durable repo↔board
    /// link and the sync's idempotency anchor: if an edit-and-save cycle drops it, the next sync
    /// re-creates the task instead of updating it. This drives the REAL write path the UI uses —
    /// <see cref="TaskService.UpdateAsync"/>, the same call <c>SaveDescriptionAsync</c> makes — and
    /// re-reads from a fresh context, so it asserts on persisted bytes rather than on the in-memory
    /// entity the service happens to return.
    /// </summary>
    [Fact]
    public async Task EditingASyncedDescription_KeepsTheSyncBlockInStorageByteForByte()
    {
        using var f = new SqliteTestFactory();
        var original = SyncedDescription("## Summary\n\nOriginal body.");

        int taskId;
        using (var db = f.CreateDbContext())
        {
            var project = new Project { Name = "P" };
            var feature = new Feature { Name = "F", Project = project };
            var task = new TaskItem { Name = "#72 mirrored", Feature = feature, Description = original };
            db.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        // What the UI hands back after a user types at the end of the raw source in the textarea.
        var intended = original + "\n\nAdded by a human.";
        var edited = intended + "  \r\n\t";

        var svc = new TaskService(f);
        await svc.UpdateAsync(taskId, new TaskUpdate(Description: edited), actor: "tester");

        using var fresh = f.CreateDbContext();
        var stored = (await fresh.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).Description;

        Assert.NotNull(stored);
        Assert.Contains("<!-- kanbanflow-sync -->", stored, StringComparison.Ordinal);
        Assert.Contains("<!-- /kanbanflow-sync -->", stored, StringComparison.Ordinal);
        Assert.Contains($"**Issue:** {IssueUrl}", stored, StringComparison.Ordinal);
        // Byte-for-byte: the whole block, not merely its opening marker.
        Assert.StartsWith(original, stored, StringComparison.Ordinal);
        // …and the edit actually landed, so this cannot pass by the update having been a no-op.
        Assert.Contains("Added by a human.", stored, StringComparison.Ordinal);
        // TaskService owns normalization: trailing transport/editor whitespace is not persisted.
        Assert.Equal(intended, stored);
    }

}
