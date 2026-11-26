using System.Text.RegularExpressions;

namespace KanbanFlow.Tests;

/// <summary>Structural guards on card-title wrapping and the preview during a drag.</summary>
public sealed class BoardCardTitleClampCssTests
{
    private const string StylesheetPath = "src/KanbanFlow/wwwroot/app.css";

    [Theory]
    [InlineData("board-card-title", "board-card-title-text")]
    [InlineData("outline-task-title", "outline-task-title-text")]
    public void TaskTitles_WrapWithoutAVisualLineLimit(string buttonClass, string textClass)
    {
        var rules = ParseRules(RepositorySource.Read(StylesheetPath));
        var button = rules.Where(r => SplitSelectorList(r.Selector).Any(s => SubjectClasses(s).Contains(buttonClass)));
        var text = rules.Where(r => SplitSelectorList(r.Selector).Any(s => SubjectClasses(s).Contains(textClass)));
        Assert.Contains(button, r => Regex.IsMatch(r.Body, @"overflow-wrap\s*:\s*anywhere"));
        Assert.Contains(text, r => Regex.IsMatch(r.Body, @"display\s*:\s*block"));
        Assert.DoesNotContain(button.Concat(text), r => Regex.IsMatch(r.Body,
            @"(?:line-clamp\s*:\s*\d|white-space\s*:\s*nowrap)"));
    }

    /// <summary>Drag-suppression rules are scoped to <c>@media (hover: hover)</c> so they do not reach the touch always-open branch.</summary>
    [Fact]
    public void DragSuppression_DoesNotReachTheTouchAlwaysOpenBranch()
    {
        var rules = ParseRules(RepositorySource.Read(StylesheetPath));

        var touchBranch = rules.Where(r => r.AtRules.Any(IsHoverNone)).ToList();
        // NON-VACUITY: there IS an always-open touch branch for the suppression to collide with.
        Assert.NotEmpty(touchBranch);

        var suppression = DragSuppressionRules(rules);
        Assert.NotEmpty(suppression);

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
        Assert.NotEmpty(suppression);

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

}
