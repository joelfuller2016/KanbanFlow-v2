using System.Text.RegularExpressions;

namespace KanbanFlow.Tests;

/// <summary>
/// Structural guards on the #481 two-line title clamp in
/// <c>src/KanbanFlow/wwwroot/app.css</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THESE CAN AND CANNOT DO. There is no CSS engine in this process, so nothing here proves a
/// rendered height. What each case proves is a STRUCTURAL precondition that the rendering depends
/// on — the kind of precondition whose violation is invisible in the browser the author happens to
/// be using, which is exactly how the guarded defect shipped:
/// </para>
/// <list type="number">
///   <item><description>
///     The clamp was declared on a <c>&lt;button&gt;</c>. WebKit does not apply
///     <c>display: -webkit-box</c> to a button, so <c>-webkit-line-clamp</c> on one is inert there
///     while working perfectly in Chromium and Gecko. Measured with Playwright against this
///     stylesheet and this markup, on a 280px column: with the clamp on the button, WebKit rendered
///     the title box at 106px (5.1 lines — no clamping at all) while Chromium and Firefox rendered
///     43.6px (2.1 lines); with the clamp on an inner span, all three render 43.6px. #481's
///     complaint is that a card shows too little title to identify — the pre-fix rule shipped that
///     bug unchanged to every WebKit reader, which is every browser on iOS.
///   </description></item>
/// </list>
/// <para>
/// The stylesheet is parsed rather than string-matched so the assertions can talk about a rule's
/// SUBJECT and its enclosing at-rules, which is where the defect lived.
/// </para>
/// </remarks>
public sealed class BoardCardTitleClampCssTests
{
    private const string StylesheetPath = "src/KanbanFlow/wwwroot/app.css";

    /// <summary>The markup that has to agree with the stylesheet about where the clamp lands.</summary>
    public static TheoryData<string> TitleMarkupFiles => new(
        "src/KanbanFlow/Components/Shared/KanbanCard.razor",
        "src/KanbanFlow/Components/Shared/ProjectOutline.razor");

    // ── The clamp ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TitleMarkupFiles))]
    public void TheLineClamp_IsNeverAppliedToAButton(string razorPath)
    {
        var clampContainers = ClampContainerClasses();
        // NON-VACUITY: an "is never on a button" check passes trivially against an empty set.
        Assert.NotEmpty(clampContainers);

        var markup = StripRazorAndHtmlComments(RepositorySource.Read(razorPath));
        var carriers = clampContainers
            .SelectMany(c => ElementsCarryingClass(markup, c).Select(tag => (Class: c, Tag: tag)))
            .ToList();

        // NON-VACUITY: this file really does carry one of the clamp containers, so the check below
        // is about this markup rather than about a class that lives somewhere else entirely.
        Assert.NotEmpty(carriers);

        var onButtons = carriers.Where(p => p.Tag == "button").Select(p => p.Class).Distinct().ToList();
        Assert.True(onButtons.Count == 0,
            $"{razorPath} applies the two-line clamp directly to a <button> ({string.Join(", ", onButtons)}). "
            + "WebKit does not apply `display: -webkit-box` to a button — a button's children are laid out "
            + "in an anonymous box the property never reaches — so the clamp is silently inert in Safari and "
            + "in every browser on iOS, while still working in Chromium and Gecko. Measured on a 280px "
            + "column: 106px (5.1 unclamped lines) in WebKit against 43.6px (2 lines) in Chromium. #481 is "
            + "the bug that a card shows too little of its title to identify a task; a clamp the reader's "
            + "browser ignores ships that bug to them unchanged. Put the clamp on a block container INSIDE "
            + "the button (`.board-card-title-text` / `.outline-task-title-text`) instead.");
    }

    [Fact]
    public void EveryLineClampDeclaration_TargetsTheBoxThatCarriesTheClamp()
    {
        var rules = ParseRules(RepositorySource.Read(StylesheetPath));
        var clampContainers = ClampContainerClasses(rules);
        Assert.NotEmpty(clampContainers);

        var clampRules = rules.Where(r => Regex.IsMatch(r.Body, @"-webkit-line-clamp\s*:")).ToList();
        // NON-VACUITY: the clamp definition plus its hover, focus, touch and drag overrides. If this
        // drops below four, the overrides have gone missing and the loop is guarding nothing.
        Assert.True(clampRules.Count >= 4,
            $"Expected the clamp definitions and their hover/focus/touch/drag overrides, found {clampRules.Count} "
            + "rules declaring -webkit-line-clamp. Re-derive this guard rather than lowering the number.");

        foreach (var rule in clampRules)
        {
            foreach (var selector in SplitSelectorList(rule.Selector))
            {
                Assert.True(SubjectClasses(selector).Overlaps(clampContainers),
                    $"`{selector.Trim()}` sets -webkit-line-clamp on something that is not a clamp container "
                    + $"({string.Join(", ", clampContainers.Order())}). The clamp is only established where "
                    + "`display: -webkit-box` is, so a rule aimed anywhere else is dead: clamping does nothing "
                    + "and, worse, UN-clamping does nothing — the hover and touch reveals would stop showing "
                    + "the full title while looking, in the stylesheet, as though they still did.");
            }
        }
    }

    // ── Reading the stylesheet ───────────────────────────────────────────────────────────────────

    /// <summary>A style rule with the at-rules (media queries, etc.) it is nested inside.</summary>
    private sealed record CssRule(IReadOnlyList<string> AtRules, string Selector, string Body);

    /// <summary>
    /// The classes that establish a line clamp — the subjects of every rule declaring
    /// <c>display: -webkit-box</c>. `-webkit-line-clamp` has no effect anywhere else.
    /// </summary>
    private static HashSet<string> ClampContainerClasses(IEnumerable<CssRule>? rules = null) =>
        (rules ?? ParseRules(RepositorySource.Read(StylesheetPath)))
            .Where(r => Regex.IsMatch(r.Body, @"display\s*:\s*-webkit-box"))
            .SelectMany(r => SplitSelectorList(r.Selector))
            .SelectMany(SubjectClasses)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A deliberately small stylesheet reader: enough to know a rule's subject and which at-rules
    /// it sits inside, which is all these guards ask about.
    /// </summary>
    /// <remarks>
    /// Comments come out first — app.css's prose contains braces, and a brace-counting reader that
    /// believed them would mis-nest every rule after the first one. Selector lists are split on a
    /// bare comma and compounds on whitespace/combinators, which is correct for this stylesheet
    /// (no <c>:is()</c>/<c>:where()</c> lists, and its one <c>:has()</c> holds a single compound).
    /// If that stops being true, this reader is what needs extending — not the assertions.
    /// </remarks>
    private static List<CssRule> ParseRules(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

        var rules = new List<CssRule>();
        var atRules = new List<string>();
        var prelude = new System.Text.StringBuilder();

        for (var i = 0; i < css.Length;)
        {
            var c = css[i];
            if (c == '{')
            {
                var head = prelude.ToString().Trim();
                prelude.Clear();
                if (head.StartsWith('@'))
                {
                    atRules.Add(head);
                    i++;
                    continue;
                }

                // Style rules do not nest in this stylesheet, so the next '}' closes this one.
                var close = css.IndexOf('}', i);
                Assert.True(close > 0, $"Unbalanced braces in {StylesheetPath} at offset {i}.");
                rules.Add(new CssRule([.. atRules], head, css[(i + 1)..close]));
                i = close + 1;
            }
            else if (c == '}')
            {
                if (atRules.Count > 0) atRules.RemoveAt(atRules.Count - 1);
                prelude.Clear();
                i++;
            }
            else if (c == ';')
            {
                prelude.Clear();   // a statement at-rule (@charset / @import); nothing to record
                i++;
            }
            else
            {
                prelude.Append(c);
                i++;
            }
        }

        // NON-VACUITY for every case above: a reader that silently produced nothing would make all
        // four of them pass by having nothing to look at.
        Assert.True(rules.Count > 100, $"Only parsed {rules.Count} rules out of {StylesheetPath}.");
        Assert.Contains(rules, r => r.AtRules.Count > 0);
        return rules;
    }

    private static IEnumerable<string> SplitSelectorList(string selector) =>
        selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The classes of a selector's SUBJECT — its rightmost compound. `.a .b` styles `.b`; asking
    /// about every class mentioned would have called `.board-card:hover .board-card-title` a rule
    /// about `.board-card`.
    /// </summary>
    private static HashSet<string> SubjectClasses(string selector)
    {
        var compounds = selector.Split([' ', '\t', '\r', '\n', '>', '+', '~'], StringSplitOptions.RemoveEmptyEntries);
        var subject = compounds.Length > 0 ? compounds[^1] : selector;
        return Regex.Matches(subject, @"\.(-?[A-Za-z_][\w-]*)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    // ── Reading the markup ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Comments are stripped for the same reason the stylesheet's are: this file's own commentary
    /// names both the class and the <c>&lt;button&gt;</c> it must not be on, and a reader that
    /// believed prose would report the explanation as the defect.
    /// </summary>
    private static string StripRazorAndHtmlComments(string markup) =>
        Regex.Replace(Regex.Replace(markup, @"@\*.*?\*@", "", RegexOptions.Singleline),
            @"<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>
    /// The lowercased tag name of every element whose opening tag carries <paramref name="className"/>
    /// as a whole class token.
    /// </summary>
    /// <remarks>
    /// Walks back to the opening <c>&lt;</c> rather than parsing attributes: a Razor class attribute
    /// interpolates <c>@(cond ? "a" : "")</c>, whose nested quotes defeat any <c>class="[^"]*"</c>
    /// match. The token boundary is <c>[\w-]</c> rather than <c>\b</c> — <c>\b</c> considers
    /// `board-card-title` to end inside `board-card-title-text`, which is the one distinction this
    /// whole file turns on.
    /// </remarks>
    private static IEnumerable<string> ElementsCarryingClass(string markup, string className)
    {
        foreach (Match m in Regex.Matches(markup, $@"(?<![\w-]){Regex.Escape(className)}(?![\w-])"))
        {
            var open = markup.LastIndexOf('<', m.Index);
            if (open < 0) continue;

            var close = markup.IndexOf('>', open);
            if (close >= 0 && close < m.Index) continue;   // not inside an opening tag

            var tag = Regex.Match(markup[(open + 1)..], @"^[A-Za-z][\w-]*");
            if (tag.Success) yield return tag.Value.ToLowerInvariant();
        }
    }
}
