using AngleSharp.Html.Parser;
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
/// Most of them exercise the REAL renderer (real Markdig pipeline + real Ganss.Xss.HtmlSanitizer)
/// against real hostile markup - no stubs, no fakes, no mocks. Every renderer-path security
/// assertion is written so that removing the <c>Sanitize</c> call makes it FAIL (measured: 23 red).
///
/// The two dependency guards at the end are the exception: they build their OWN HtmlSanitizer with a
/// widened allow-list and never call <c>RenderToSafeHtml</c>, because the renderer's own allow-list
/// destroys the CVE payload before the parser bug can surface. They stay green under a
/// sanitizer-bypass mutation by design - what they detect is an AngleSharp DOWNGRADE.
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
    [InlineData("<p style=\"width:expression(alert(1))\">x</p>", "expression(")]
    [InlineData("<p style=\"background:url(javascript:alert(1))\">x</p>", "javascript:")]
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
    /// no active <c>onerror</c>, <c>alert(</c> or <c>&lt;script&gt;</c> survives sanitisation.
    ///
    /// IMPORTANT - measured correction: this test does NOT distinguish the vulnerable dependency
    /// from the patched one. Run against HtmlSanitizer 9.0.967 / AngleSharp 0.17.1 it PASSES 3/3,
    /// because HtmlSanitizer still strips the <c>onerror</c> attribute on the mis-parsed DOM; what
    /// the old parser actually leaks is the ELEMENT, not the handler. So this is a useful
    /// belt-and-braces assertion, but it is not the downgrade guard - do not read it as one.
    /// <see cref="AnnotationXmlIntegrationPoint_DoesNotLeakMarkupOutOfStyle_Cve202654570"/> is the
    /// test that actually goes RED on 0.17.1 (measured: Failed 2, Passed 0).
    /// </summary>
    [Theory]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><title><a encoding=\"</title><img src=x onerror=alert()>\"></annotation-xml></math>")]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><style><img src=x onerror=alert(1)></style></annotation-xml></math>")]
    [InlineData("<math><annotation-xml encoding=\"application/xhtml+xml\"><script>alert(1)</script></annotation-xml></math>")]
    public void AnnotationXmlMxssVector_IsNeutralisedWithMathmlAllowList(string payload)
    {
        // Build a sanitizer that keeps math + annotation-xml so the allow-list does not destroy
        // the payload before the parser has a chance to route it. On AngleSharp < 1.5.0 the parser
        // misroutes annotation-xml tokens, so markup nested inside is mis-parsed and the serialized
        // output leaks the injected ELEMENT (an <img src="x"> hoisted out of <style>) - note it does
        // NOT leak the handler, because HtmlSanitizer still strips onerror off the mis-parsed DOM.
        // That is exactly why the assertions below cannot detect a downgrade on their own; see
        // AnnotationXmlIntegrationPoint_DoesNotLeakMarkupOutOfStyle_Cve202654570, which asserts on
        // the leaked element and does.
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("math");
        sanitizer.AllowedTags.Add("annotation-xml");
        sanitizer.AllowedAttributes.Add("encoding");

        var sanitized = sanitizer.Sanitize(payload);

        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    // ── CVE path itself: proves the PATCHED parser is present, not just the allow-list ──────────

    /// <summary>
    /// The test above is neutralised by the allow-list on BOTH the vulnerable and the patched
    /// AngleSharp, so on its own it cannot prove the CVE is closed. This one can: it deliberately
    /// ALLOWS the MathML containers the advisory needs, so the annotation-xml integration-point bug
    /// actually executes instead of being short-circuited.
    ///
    /// On AngleSharp 0.17.1, <c>annotation-xml</c> is not treated as an HTML integration point, so
    /// the &lt;style&gt; content is parsed as markup rather than raw text and the &lt;img&gt; is
    /// HOISTED OUT of &lt;style&gt;, surviving as a sibling:
    ///   <c>&lt;annotation-xml ...&gt;&lt;style&gt;&lt;/style&gt;&lt;img src="x"&gt;&lt;/annotation-xml&gt;</c>
    /// On AngleSharp 1.6.0 the element stays inside &lt;style&gt; as text and is dropped entirely:
    ///   <c>&lt;annotation-xml ...&gt;&lt;style&gt;&lt;/style&gt;&lt;/annotation-xml&gt;</c>
    ///
    /// Both measured directly against 9.0.967/0.17.1 and 9.1.974/1.6.0. So this test is RED on the
    /// vulnerable dependency and GREEN on the patched one - it is the regression guard that fails if
    /// AngleSharp is ever downgraded back below 1.5.0.
    /// </summary>
    [Theory]
    [InlineData("<math><annotation-xml encoding=\"text/html\"><style><img src=x onerror=alert(1)></style></annotation-xml></math>")]
    [InlineData("<math><semantics><annotation-xml encoding=\"text/html\"><style><img src=x onerror=alert(1)></style></annotation-xml></semantics></math>")]
    public void AnnotationXmlIntegrationPoint_DoesNotLeakMarkupOutOfStyle_Cve202654570(string payload)
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "math", "semantics", "annotation-xml", "style", "img" })
            sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Add("encoding");
        sanitizer.AllowedAttributes.Add("src");
        // img/src ARE in HtmlSanitizer's default allow-list today, but they are pinned explicitly
        // because the detection depends on them: if a future HtmlSanitizer minor dropped either
        // default, the <img> would be stripped for the WRONG reason and this guard would go green
        // against a downgraded AngleSharp - a guard that silently stops being able to fail, which
        // is the exact failure mode #320 exists to remove.

        var html = sanitizer.Sanitize(payload);

        // The load-bearing assertion: on AngleSharp < 1.5.0 the <img> escapes <style> and appears
        // here. On >= 1.5.0 it never materialises as an element at all.
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        // And the advisory's actual impact must not appear under any parse.
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        // The advisory is a serialise -> RE-PARSE mismatch, so assert on the DOM a browser would
        // actually build from this output, not only on the string we happened to serialise.
        var reparsed = new HtmlParser().ParseDocument("<!doctype html><body>" + html);
        Assert.Empty(reparsed.QuerySelectorAll("img"));
        Assert.Empty(reparsed.QuerySelectorAll("script"));
        Assert.DoesNotContain(
            reparsed.All.SelectMany(e => e.Attributes),
            a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));
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
    /// (9.1.974 / AngleSharp 1.6.0): 13/13 identical, so neither the AngleSharp 0.17 -> 1.6 jump nor
    /// the AngleSharp.Css 0.17 -> 1.0 jump changed how benign markdown serialises. Pinning them here
    /// means a future dependency bump that DOES alter rendering fails the gate instead of silently
    /// shipping. The last two cover the <c>style</c> attribute specifically, because AngleSharp.Css
    /// went up a full major and owns CSS sanitisation - the riskiest untested half of this bump.
    /// (The CSS serialiser normalises: <c>color:red</c> comes back as <c>rgba(255, 0, 0, 1)</c>.
    /// Identical on both versions, so it is pinned, not a regression.)
    ///
    /// KNOWN, DELIBERATELY PINNED LOSSES - these fixtures record what the allow-list already strips
    /// today; they are NOT an endorsement of it. <c>AllowedAttributes</c> contains neither
    /// <c>class</c> nor <c>id</c>, so the fenced-code fixture loses the language class (no
    /// client-side syntax highlighting), the heading fixture loses its id (in-page anchors are
    /// dead), and the task-list fixture loses its CSS hooks. All pre-date #320. If a later change
    /// correctly allow-lists those attributes these three fixtures SHOULD fail - update them, do
    /// not read the failure as a regression.
    ///
    /// AND ONE THING DELIBERATELY KEPT, for symmetry: the two style fixtures pin <c>style</c> as an
    /// ALLOWED attribute on user-supplied markdown. That is HtmlSanitizer's default and pre-dates
    /// #320; it is recorded here so nobody reads the class/id note above and assumes every
    /// attribute is stripped. It is not free-form - HtmlSanitizer runs the CSS through
    /// AllowedCssProperties (239 entries), which is why the two hostile CSS cases in
    /// DangerousUrlSchemes_AreDropped (expression(...) and url(javascript:...)) are neutralised.
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
    [InlineData("<p style=\"color:red\">x</p>", "<p style=\"color: rgba(255, 0, 0, 1)\">x</p>\n")]
    [InlineData("<p style=\"color:red;font-weight:bold\">x</p>", "<p style=\"color: rgba(255, 0, 0, 1); font-weight: bold\">x</p>\n")]
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
