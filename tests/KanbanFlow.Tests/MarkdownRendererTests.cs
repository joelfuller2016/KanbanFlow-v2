using KanbanFlow.Services;

namespace KanbanFlow.Tests;

/// <summary>
/// Gated security tests for <see cref="MarkdownRenderer"/> (#320). This is the only XSS defence in
/// the app: <c>Components/Shared/MarkdownContent.razor</c> holds the sole <c>MarkupString</c> in the
/// whole component tree and feeds it straight from <see cref="MarkdownRenderer.RenderToSafeHtml"/>.
/// Before #320 nothing in the gated suite referenced this type at all, so bypassing the sanitizer
/// outright still left the merge gate at 449/449 green (measured). These tests close that hole.
///
/// They exercise the REAL renderer (real Markdig pipeline + real Ganss.Xss.HtmlSanitizer) against
/// real hostile markup - no stubs, no fakes, no mocks. Every security assertion is written so that
/// removing the <c>Sanitize</c> call makes it FAIL.
/// </summary>
public sealed class MarkdownRendererTests
{
    private static string Render(string? markdown) => new MarkdownRenderer().RenderToSafeHtml(markdown);

    // ── Script injection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScriptTag_IsStripped_ButSurroundingTextSurvives()
    {
        var html = Render("<script>alert(1)</script>hello");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", html, StringComparison.OrdinalIgnoreCase);
        // Guards the opposite failure mode: a "fix" that just returns string.Empty.
        Assert.Contains("hello", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [InlineData("<object data=\"evil.swf\"></object>", "<object")]
    [InlineData("<embed src=\"evil.swf\">", "<embed")]
    [InlineData("<style>body{background:url(javascript:alert(1))}</style>", "<style")]
    [InlineData("<svg><animate onbegin=alert(1) attributeName=x></svg>", "<svg")]
    [InlineData("<form action=\"https://evil.example\"><input name=x></form>", "<form")]
    public void DangerousTags_AreStripped(string markdown, string forbidden)
    {
        Assert.DoesNotContain(forbidden, Render(markdown), StringComparison.OrdinalIgnoreCase);
    }

    // ── Event-handler attributes ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("<a href=\"https://x.example\" onclick=\"steal()\">x</a>", "onclick")]
    [InlineData("<img src=x onload=alert(1)>", "onload")]
    [InlineData("<p onmouseover=\"alert(1)\">hover</p>", "onmouseover")]
    [InlineData("<a href=\"https://x.example\" onfocus=\"alert(1)\" autofocus>x</a>", "onfocus")]
    public void EventHandlerAttributes_AreStripped(string markdown, string forbidden)
    {
        Assert.DoesNotContain(forbidden, Render(markdown), StringComparison.OrdinalIgnoreCase);
    }

    // ── Dangerous URL schemes ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[click me](javascript:alert(1))", "javascript:")]
    [InlineData("<a href=\"javascript:alert(1)\">click me</a>", "javascript:")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">click me</a>", "vbscript:")]
    [InlineData("<img src=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">", "data:text/html")]
    public void DangerousUrlSchemes_AreDropped(string markdown, string forbidden)
    {
        Assert.DoesNotContain(forbidden, Render(markdown), StringComparison.OrdinalIgnoreCase);
    }

    // ── CVE-2026-54570 / GHSA-pgww-w46g-26qg regression guard ───────────────────────────────────

    /// <summary>
    /// The advisory this bump closes: AngleSharp &lt; 1.5.0 never flagged
    /// <c>MathAnnotationXmlElement</c> as an HTML integration point, so tokens routed through
    /// <c>Foreign()</c> instead of <c>Home()</c>, and <c>HtmlMarkupFormatter.WriteAttributeValue()</c>
    /// did not escape &lt;/&gt; in attribute values. Net effect: a sanitizer walking the DOM sees
    /// nothing dangerous while the SERIALISED output re-parses in a browser as a live element.
    ///
    /// Note on scope, so this test is not over-read: measured against the pre-bump build, this app's
    /// own allow-list already reduced every payload below to an empty paragraph, because
    /// <c>AllowedTags</c> is cleared and neither <c>math</c> nor <c>annotation-xml</c> is re-added.
    /// That is a lucky side effect of the configuration, not a designed mitigation - it evaporates
    /// the moment somebody widens the allow-list. This is therefore a REGRESSION guard: it pins the
    /// neutralised behaviour so a future allow-list change or sanitizer bypass turns the gate red.
    /// </summary>
    [Theory]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><style><img src=x onerror=alert(1)></style></annotation-xml></math>")]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><a title=\"</annotation-xml><img src=x onerror=alert(1)>\"></a></annotation-xml></math>")]
    [InlineData("<annotation-xml encoding=\"text/html\"><img src=x onerror=alert(1)></annotation-xml>")]
    [InlineData("<math><annotation-xml encoding=\"application/xhtml+xml\"><script>alert(1)</script></annotation-xml></math>")]
    public void AnnotationXmlMxssVector_IsNeutralised(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("annotation-xml", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Empty input contract ────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullInput_ReturnsEmpty() => Assert.Equal(string.Empty, Render(null));

    [Fact]
    public void EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, Render(string.Empty));

    // ── Legitimate markdown still renders (over-stripping guard, #320 goal 6) ───────────────────

    /// <summary>
    /// Exact-output fixtures. These were captured from the PRE-bump build (HtmlSanitizer 9.0.967 /
    /// AngleSharp 0.17.1) and re-verified byte-for-byte against the POST-bump build
    /// (9.1.974 / AngleSharp 1.6.0): 11/11 identical, so the AngleSharp 0.17 -> 1.6 major jump
    /// changed nothing about how benign markdown serialises. Pinning them here means a future
    /// dependency bump that DOES alter rendering fails the gate instead of silently shipping.
    /// </summary>
    [Theory]
    [InlineData("This is **bold** text.", "<p>This is <strong>bold</strong> text.</p>\n")]
    [InlineData("This is *italic* text.", "<p>This is <em>italic</em> text.</p>\n")]
    [InlineData("A [link](https://example.com) here.", "<p>A <a href=\"https://example.com\">link</a> here.</p>\n")]
    [InlineData("```csharp\nvar x = 1;\n```", "<pre><code>var x = 1;\n</code></pre>\n")]
    [InlineData("- one\n- two\n- three", "<ul>\n<li>one</li>\n<li>two</li>\n<li>three</li>\n</ul>\n")]
    [InlineData("| A | B |\n|---|---|\n| 1 | 2 |", "<table>\n<thead>\n<tr>\n<th>A</th>\n<th>B</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>1</td>\n<td>2</td>\n</tr>\n</tbody>\n</table>\n")]
    [InlineData("# Title\n\nBody paragraph.", "<h1>Title</h1>\n<p>Body paragraph.</p>\n")]
    [InlineData("Use `dotnet test` now.", "<p>Use <code>dotnet test</code> now.</p>\n")]
    [InlineData("~~gone~~ stays", "<p><del>gone</del> stays</p>\n")]
    [InlineData("- [x] done\n- [ ] todo", "<ul>\n<li><input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\"> done</li>\n<li><input disabled=\"disabled\" type=\"checkbox\"> todo</li>\n</ul>\n")]
    [InlineData("> quoted text", "<blockquote>\n<p>quoted text</p>\n</blockquote>\n")]
    public void BenignMarkdown_RendersExactly(string markdown, string expected)
    {
        Assert.Equal(expected, Render(markdown));
    }

    [Fact]
    public void SafeHttpLink_KeepsHref()
    {
        // The sanitizer must not be so aggressive that ordinary links stop working - otherwise a
        // failing XSS test could be "fixed" by stripping everything.
        var html = Render("[docs](https://example.com/page)");

        Assert.Contains("href=\"https://example.com/page\"", html, StringComparison.Ordinal);
        Assert.Contains("docs", html, StringComparison.Ordinal);
    }
}
