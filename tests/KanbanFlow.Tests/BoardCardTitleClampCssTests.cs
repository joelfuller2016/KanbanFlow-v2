using System.Text.RegularExpressions;

namespace KanbanFlow.Tests;

/// <summary>
/// Structural guards on the #481 title clamp and its drag suppression in
/// <c>src/KanbanFlow/wwwroot/app.css</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THESE CAN AND CANNOT DO. There is no CSS engine in this process, so nothing here proves a
/// rendered height. What each case proves is a STRUCTURAL precondition that the rendering depends
/// on — the kind of precondition whose violation is invisible in the browser the author happens to
/// be using, which is exactly how both guarded defects shipped:
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
///   <item><description>
///     The drag-suppression rules out-specified the touch always-open branch, so beginning a drag
///     on a touch device re-clamped and re-collapsed EVERY card at once — the whole-board reflow
///     the suppression exists to prevent — and, with no <c>transition</c> reset, animated it for
///     180ms after the drag was already under way.
///   </description></item>
/// </list>
/// <para>
/// The stylesheet is parsed rather than string-matched so the assertions can talk about a rule's
/// SUBJECT and its enclosing at-rules, which is where both defects lived.
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

    /// <summary>The two-line title clamp is never applied directly to a <c>&lt;button&gt;</c> element.</summary>
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

    /// <summary>Every <c>-webkit-line-clamp</c> declaration's subject class belongs to the set of classes that establish a <c>display: -webkit-box</c>.</summary>
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

    // ── The drag suppression ─────────────────────────────────────────────────────────────────────

    /// <summary>Drag-suppression rules are scoped to <c>@media (hover: hover)</c> so they do not reach the touch always-open branch.</summary>
    [Fact]
    public void DragSuppression_DoesNotReachTheTouchAlwaysOpenBranch()
    {
        var rules = ParseRules(RepositorySource.Read(StylesheetPath));

        var touchBranch = rules.Where(r => r.AtRules.Any(IsHoverNone)).ToList();
        // NON-VACUITY: there IS an always-open touch branch for the suppression to collide with.
        Assert.NotEmpty(touchBranch);

        var suppression = DragSuppressionRules(rules);
        Assert.True(suppression.Count >= 2,
            $"Expected the reveal and the title to both be suppressed during a drag, found {suppression.Count} "
            + "rule(s) keyed on `.kanban-ghost`.");

        foreach (var rule in suppression)
        {
            Assert.True(rule.AtRules.Any(IsHoverHover),
                $"`{rule.Selector.Trim()}` suppresses the reveal during a drag but is not scoped to "
                + "`@media (hover: hover)`, so it also applies on touch. On touch the reveal is permanently "
                + "open and the title permanently unclamped, and this rule out-specifies both — so the "
                + "instant a drag begins it re-clamps the title and collapses the reveal on EVERY card at "
                + "once. That is a full-board reflow delivered at the exact moment the user is dragging, "
                + "caused by the rule whose entire purpose is to keep the geometry still. Touch needs no "
                + "suppression: with nothing growing on hover there, there is nothing to suppress.");
        }
    }

    /// <summary>Drag-suppression rules reset <c>transition: none</c> so the collapse is instantaneous.</summary>
    [Fact]
    public void DragSuppression_CollapsesInOneFrame()
    {
        var rules = ParseRules(RepositorySource.Read(StylesheetPath));

        // NON-VACUITY: the reveal is tweened at rest, so `transition: none` is a real reset rather
        // than a no-op restating a default.
        var restingReveal = rules.Single(r => r.AtRules.Count == 0 && r.Selector.Trim() == ".board-card-reveal");
        Assert.Matches(@"transition\s*:\s*grid-template-rows", restingReveal.Body);

        var suppression = DragSuppressionRules(rules);
        Assert.True(suppression.Count >= 2, $"Only {suppression.Count} drag-suppression rule(s) found.");

        foreach (var rule in suppression)
        {
            Assert.True(Regex.IsMatch(rule.Body, @"transition\s*:\s*none"),
                $"`{rule.Selector.Trim()}` suppresses the reveal for the duration of a drag but does not reset "
                + "`transition`, so the collapse it asks for is TWEENED over the .18s the resting rule "
                + "declares. SortableJS resolves a drop from the list's live geometry, so 180ms of moving "
                + "geometry starting at the moment of the drag is precisely the failure this suppression "
                + "exists to prevent — it merely delays it instead of stopping it. `transition: none` makes "
                + "the collapse happen in the frame the drag begins.");
        }
    }

    // ── Reading the stylesheet ───────────────────────────────────────────────────────────────────

    /// <summary>A style rule with the at-rules (media queries, etc.) it is nested inside.</summary>
    private sealed record CssRule(IReadOnlyList<string> AtRules, string Selector, string Body);

    /// <summary>
    /// The rules that style something ELSE because a drag is in flight — selectors that mention
    /// SortableJS's ghost class as a condition rather than as their subject. The exclusion matters:
    /// `.kanban-ghost` itself is also a rule (the drag placeholder's own appearance), and it is not
    /// a suppression rule, so folding it in would make both cases below fail for the wrong reason.
    /// </summary>
    private static List<CssRule> DragSuppressionRules(IEnumerable<CssRule> rules) =>
        rules.Where(r => r.Selector.Contains("kanban-ghost", StringComparison.Ordinal)
                         && SplitSelectorList(r.Selector).All(s => !SubjectClasses(s).Contains("kanban-ghost")))
             .ToList();

    /// <summary>Returns true when the at-rule is <c>@media (hover: hover)</c>.</summary>
    private static bool IsHoverHover(string atRule) =>
        Regex.IsMatch(atRule, @"^@media\b[^{]*\(\s*hover\s*:\s*hover\s*\)");

    /// <summary>Returns true when the at-rule is <c>@media (hover: none)</c>.</summary>
    private static bool IsHoverNone(string atRule) =>
        Regex.IsMatch(atRule, @"^@media\b[^{]*\(\s*hover\s*:\s*none\s*\)");

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

    /// <summary>Splits a CSS selector list on bare commas.</summary>
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
