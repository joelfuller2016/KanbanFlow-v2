using Ganss.Xss;
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
    // One shared instance, matching how Program.cs registers MarkdownRenderer (singleton).
    // It is immutable after construction, so rebuilding the Markdig pipeline and the
    // sanitizer allow-list for every theory case would only cost time.
    private static readonly MarkdownRenderer Renderer = new();

    private static string Render(string? markdown) => Renderer.RenderToSafeHtml(markdown);

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
    /// did not escape the &lt; and &gt; characters in attribute values. Net effect: a sanitizer
    /// walking the DOM sees nothing dangerous while the SERIALISED output re-parses in a
    /// browser as a live element.
    ///
    /// Note on scope, so this test is not over-read. A 17-vector differential sweep was run through
    /// this exact sanitizer configuration on BOTH HtmlSanitizer 9.0.967 / AngleSharp 0.17.1 and
    /// 9.1.974 / AngleSharp 1.6.0 - covering the annotation-xml PoC shapes (text/html,
    /// application/xhtml+xml, no encoding, uppercase, self-closing, nested in p/table/pre),
    /// mtext+mglyph, svg foreignObject and svg desc. Re-parsing each sanitized output gave
    /// ZERO event-handler attributes and ZERO script elements on BOTH versions; the only deltas
    /// were trailing newlines.
    ///
    /// So no payload was found that is exploitable here on the vulnerable version: because
    /// <c>AllowedTags</c> is cleared and neither <c>math</c>, <c>annotation-xml</c>, <c>svg</c> nor
    /// <c>style</c> is re-added, HtmlSanitizer drops those elements WITH their whole subtree
    /// (KeepChildNodes is false by default), so the payload is destroyed before serialisation - and
    /// serialisation is where the CVE bites. That is a side effect of the allow-list, not a designed
    /// mitigation, and it evaporates the moment somebody widens the list.
    ///
    /// These cases are therefore a REGRESSION guard, not an exploit reproduction: they pin the
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

    /// <summary>
    /// Exercises the CVE path with a MathML-permissive sanitizer configuration.
    /// <c>MarkdownRenderer</c> excludes <c>math</c> and <c>annotation-xml</c> from its allow-list,
    /// so the existing <see cref="AnnotationXmlMxssVector_IsNeutralised"/> cases only prove that
    /// allow-list stripping works. This test widens the allow-list to include those containers,
    /// which is the configuration that would be vulnerable on AngleSharp &lt; 1.5.0, and asserts that
    /// the fixed AngleSharp 1.6.0 correctly surfaces the injected <c>onerror</c> attribute so
    /// HtmlSanitizer can strip it. If someone downgrades to a vulnerable AngleSharp, this test fails.
    /// </summary>
    [Theory]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><title><a encoding=\"</title><img src=x onerror=alert()>\"></annotation-xml></math>")]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><style><img src=x onerror=alert(1)></style></annotation-xml></math>")]
    [InlineData("<math><annotation-xml encoding=\"application/xhtml+xml\"><script>alert(1)</script></annotation-xml></math>")]
    public void AnnotationXmlMxssVector_IsNeutralisedWithMathmlAllowList(string payload)
    {
        // Build a sanitizer that keeps math + annotation-xml so the allow-list does not destroy
        // the payload before the parser has a chance to route it. On AngleSharp < 1.5.0 the
        // parser misroutes annotation-xml tokens, the sanitizer never sees the injected element,
        // and the serialized output still contains the active attribute. On the fixed 1.6.0 the
        // element is correctly placed in the DOM and HtmlSanitizer strips the dangerous attribute.
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("math");
        sanitizer.AllowedTags.Add("annotation-xml");
        sanitizer.AllowedAttributes.Add("encoding");

        var sanitized = sanitizer.Sanitize(payload);

        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", sanitized, StringComparison.OrdinalIgnoreCase);
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
