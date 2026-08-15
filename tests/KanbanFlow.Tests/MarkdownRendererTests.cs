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
/// assertion is written so that removing the <c>Sanitize</c> call makes it FAIL (measured: 27 red, all in this class).
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

        // RE-PARSE FIRST, deliberately. The advisory is a serialise -> re-parse mismatch, so the
        // DOM is the property under test and these must be the assertions that fire. Ordered after
        // the string checks they were SHADOWED: xUnit stops at the first failure, so on a
        // downgraded AngleSharp the substring assertion below failed and this block never ran -
        // it could not have failed, which makes it decoration rather than a guard.
        // Measured on 9.0.967/0.17.1: img=1 here, so this block genuinely fails on its own.
        //
        // Honest limit: this re-parses with AngleSharp, i.e. the component under test. A browser is
        // the real reference implementation and AngleSharp is the thing that deviates, so treat
        // this as corroboration of the DOM shape, not as a browser-equivalent oracle. It happens to
        // surface the leak on 0.17.1 (measured), but a bug AngleSharp cannot see when parsing is
        // also a bug it cannot see when re-parsing.
        var reparsed = new HtmlParser().ParseDocument("<!doctype html><body>" + html);
        Assert.Empty(reparsed.QuerySelectorAll("img"));
        Assert.Empty(reparsed.QuerySelectorAll("script"));
        Assert.DoesNotContain(
            reparsed.All.SelectMany(e => e.Attributes),
            a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase));

        // Then the serialised string, as defence-in-depth: on AngleSharp < 1.5.0 the <img> escapes
        // <style> and appears here verbatim. On >= 1.5.0 it never materialises at all.
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
    // ── Per-tag attribute scoping (#340) ────────────────────────────────────────────────────────

    /// <summary>
    /// AC1. <c>input</c> is allow-listed only so Markdig's task-list checkbox renders, but
    /// <c>value</c> is in HtmlSanitizer's DEFAULT allow-list, so before #340 a stored description
    /// or comment could paint a live, native-looking text box:
    /// <c>&lt;input type="text" value="Enter password" onfocus=alert(1) autofocus&gt;</c> came back
    /// as <c>&lt;input type="text" value="Enter password"&gt;</c>. The event handler was always
    /// stripped - the surviving FORM FIELD is the defect (UI spoofing / phishing surface).
    ///
    /// Asserted on the re-parsed DOM rather than the string, because the property under test is
    /// "does a live input render", and a substring check would also pass for a mangled-but-present
    /// element. The bare <c>&lt;input&gt;</c> and <c>&lt;input value=...&gt;</c> cases matter
    /// independently: an input with no <c>type</c> defaults to a text box in every browser, so
    /// stripping <c>type</c> alone would NOT have closed this.
    ///
    /// The last three rows carry <c>disabled</c> ON PURPOSE and are what make this theory guard
    /// BOTH halves of <c>IsTaskListCheckbox</c>. Without them every row was killed by the
    /// <c>disabled</c> half alone, so the <c>type == "checkbox"</c> half was reachable by no test:
    /// gutting it to <c>true</c> left the whole 646-test suite GREEN (measured). Under that same
    /// mutation these three render as live form fields, so they are the rows that fail.
    /// </summary>
    [Theory]
    [InlineData("<input type=\"text\" value=\"Enter password\" onfocus=alert(1) autofocus>")]
    [InlineData("<input type=\"password\" value=\"hunter2\">")]
    [InlineData("<input value=\"Enter password\">")]
    [InlineData("<input>")]
    [InlineData("<input type=\"submit\" value=\"Delete project\">")]
    [InlineData("<input type=\"checkbox\">")]
    [InlineData("<input type=\"image\" src=\"https://evil.example/x.png\">")]
    [InlineData("<input type=\"text\" disabled value=\"Enter password\">")]
    [InlineData("<input type=\"submit\" disabled value=\"Delete project\">")]
    [InlineData("<input disabled value=\"Enter password\">")]
    public void InjectedInput_DoesNotRenderAsLiveFormField(string markdown)
    {
        var html = Render(markdown);

        var inputs = new HtmlParser().ParseDocument("<!doctype html><body>" + html)
            .QuerySelectorAll("input");

        Assert.Empty(inputs);
        Assert.DoesNotContain("value=", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC2. The cross-tag leakage the old global <c>AllowedAttributes.Add("type")</c> caused. Of
    /// the five cases below, the first two (<c>p</c>/<c>type</c> and <c>a</c>/<c>checked</c>) are
    /// the ones measured in #340 against the real configuration and quoted in the issue; the other
    /// three were added here for coverage. All five survived verbatim before this fix. The
    /// surrounding text is asserted too, so the fix cannot be faked by dropping the element
    /// wholesale.
    ///
    /// The <c>td</c> case is wrapped in a real <c>&lt;table&gt;</c> deliberately. A bare
    /// <c>&lt;td type="submit"&gt;x&lt;/td&gt;</c> was tried first and is a NON-guard: a stray
    /// <c>td</c> outside table context is dropped by the parser anyway, so it stayed GREEN against
    /// the sabotaged renderer (measured: 3 of 4 cases red, that one green). All five cases here
    /// were re-measured red.
    /// </summary>
    [Theory]
    [InlineData("<p type=\"submit\">x</p>", "type")]
    [InlineData("<a href=\"https://e.example\" checked=\"checked\">x</a>", "checked")]
    [InlineData("<p disabled=\"disabled\">x</p>", "disabled")]
    [InlineData("<strong type=\"submit\">x</strong>", "type")]
    [InlineData("<table><tr><td checked=\"checked\">x</td></tr></table>", "checked")]
    public void TaskListAttributes_DoNotSurviveOnOtherTags(string markdown, string forbidden)
    {
        var html = Render(markdown);

        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC3. The other direction: the task-list checkbox must still render with its attributes
    /// intact. <see cref="BenignMarkdown_RendersExactly"/> already pins the exact serialisation;
    /// this asserts the same thing on the DOM, so a change that keeps the bytes but breaks the
    /// element (or vice versa) still turns something red.
    /// </summary>
    [Fact]
    public void TaskListCheckbox_StillRenders()
    {
        var html = Render("- [x] done\n- [ ] todo");

        var inputs = new HtmlParser().ParseDocument("<!doctype html><body>" + html)
            .QuerySelectorAll("input");

        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i =>
        {
            Assert.Equal("checkbox", i.GetAttribute("type"));
            Assert.True(i.HasAttribute("disabled"));
        });
        Assert.True(inputs[0].HasAttribute("checked"));
        Assert.False(inputs[1].HasAttribute("checked"));
    }

    /// <summary>
    /// The surviving checkbox keeps ONLY the three attributes that make it one. Guards the
    /// whitelist rather than a deny-list: a default-allowed attribute like <c>value</c>, or a
    /// future addition to <c>AllowedAttributes</c>, must not ride along on the one element that is
    /// permitted to be an input.
    /// </summary>
    [Theory]
    [InlineData("<input type=\"checkbox\" disabled value=\"smuggled\">", "value")]
    [InlineData("<input type=\"checkbox\" disabled title=\"smuggled\">", "title")]
    [InlineData("<input type=\"checkbox\" disabled style=\"color:red\">", "style")]
    [InlineData("<input type=\"checkbox\" disabled name=\"smuggled\">", "name")]
    public void SurvivingCheckbox_KeepsOnlyItsOwnAttributes(string markdown, string forbidden)
    {
        var html = Render(markdown);

        // Both kept attributes are asserted here rather than leaning on the Markdig fixture for
        // `disabled`: this theory owns the "keeps its own" half of the whitelist, so it should fail
        // on its own if either name is dropped from TaskListCheckboxAttributes.
        Assert.Contains("disabled", html, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ACCEPTED LOSS, recorded so it is a decision rather than an unnoticed side effect (#340).
    /// <c>type</c> is now globally disallowed and restored only on <c>&lt;input&gt;</c>, so a raw
    /// <c>&lt;ol type="A"&gt;</c> loses its marker style: it rendered <c>A. First</c> before this
    /// change and renders <c>1. First</c> after. Measured both directions - re-adding <c>type</c>
    /// to <c>AllowedAttributes</c> turns this test RED with <c>type="A"</c> back in the output,
    /// which is exactly the pre-#340 behaviour.
    ///
    /// Kept as a loss on purpose. Markdown has no syntax for <c>&lt;ol type&gt;</c> - it can only
    /// arrive as raw HTML - and restoring it would mean a second per-tag exception in a mechanism
    /// whose entire point is a closed set. It is also strictly less costly than the losses this
    /// file already accepts (see <see cref="BenignMarkdown_RendersExactly"/>: dead heading anchors
    /// and no fenced-code syntax highlighting, both from <c>class</c>/<c>id</c> stripping). The
    /// degradation here is cosmetic and nothing in the app emits <c>&lt;ol type&gt;</c>.
    ///
    /// If a future change decides ordered-list markers are worth a per-tag exception, this test
    /// SHOULD fail - update it deliberately, do not read the failure as a regression.
    /// </summary>
    [Theory]
    [InlineData("<ol type=\"A\"><li>First</li></ol>")]
    [InlineData("<ol type=\"i\"><li>First</li></ol>")]
    public void OrderedListTypeAttribute_IsStripped_AcceptedLoss(string markdown)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("type", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.Contains("First", html, StringComparison.Ordinal);
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
    /// shipping.
    ///
    /// KNOWN, DELIBERATELY PINNED LOSSES - these fixtures record what the allow-list already strips
    /// today; they are NOT an endorsement of it. <c>AllowedAttributes</c> contains neither
    /// <c>class</c> nor <c>id</c>, so the fenced-code fixture loses the language class (no
    /// client-side syntax highlighting), the heading fixture loses its id (in-page anchors are
    /// dead), and the task-list fixture loses its CSS hooks. All pre-date #320. If a later change
    /// correctly allow-lists those attributes these three fixtures SHOULD fail - update them, do
    /// not read the failure as a regression.
    ///
    /// <c>style</c> is now also stripped (#340 closed by #357): removing it from
    /// <c>AllowedAttributes</c> closes the CSS-overlay attack surface introduced when
    /// <c>EditableMarkdown</c> began rendering third-party GitHub issue bodies. The two raw-HTML
    /// fixtures that previously pinned the normalised <c>rgba(...)</c> output now pin the stripped
    /// output (<c>&lt;p&gt;x&lt;/p&gt;</c>). Markdown has no syntax that emits inline styles, so
    /// nothing legitimate is lost.
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
    [InlineData("<p style=\"color:red\">x</p>", "<p>x</p>\n")]
    [InlineData("<p style=\"color:red;font-weight:bold\">x</p>", "<p>x</p>\n")]
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
