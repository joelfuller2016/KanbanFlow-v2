using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;

namespace KanbanFlow.Services;

public sealed class MarkdownRenderer : IMarkdownRenderer
{
    /// <summary>
    /// The only attributes an <c>&lt;input&gt;</c> is allowed to keep. Markdig renders a task-list
    /// item as <c>&lt;input disabled="disabled" type="checkbox" /&gt;</c>, plus
    /// <c>checked="checked"</c> when ticked - that is the sole reason <c>input</c> is allow-listed
    /// at all, so it is also the whole set.
    /// </summary>
    private static readonly HashSet<string> TaskListCheckboxAttributes =
        new(StringComparer.OrdinalIgnoreCase) { "type", "disabled", "checked" };

    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        // Allow tags Markdig actually emits with UseAdvancedExtensions on. Notably:
        //   - <del> for strikethrough (~~text~~)
        //   - <input type="checkbox"> for task lists (scoped to that exact shape below)
        //   - <sup> for footnote refs
        // Missing tags here silently strip the rendered output even though Markdig produces it.
        foreach (var tag in new[]
        {
            "p", "h1", "h2", "h3", "h4", "h5", "h6",
            "a", "ul", "ol", "li",
            "code", "pre", "blockquote",
            "strong", "em", "del", "sup",
            "table", "thead", "tbody", "tr", "td", "th",
            "hr", "br", "img",
            "input"
        })
        {
            _sanitizer.AllowedTags.Add(tag);
        }
        // Markdig task lists emit <input disabled="disabled" type="checkbox" /> (plus
        // checked="checked" when ticked), so those THREE attributes have to survive.
        //
        // They are deliberately REMOVED from AllowedAttributes (#340) rather than added to it. That
        // set is GLOBAL - the library offers no per-tag allow-list - so while they sit in it they
        // are permitted on every allowed element: <p type="submit"> and <a href=... checked> both
        // survived. Scoping is enforced separately, by the two handlers below.
        //
        // Remove, not just "stop adding": all three are already in HtmlSanitizer's DEFAULT
        // AllowedAttributes (95 entries in 9.1.974, measured), so the three Add() calls this
        // replaces were no-ops and deleting them alone would have changed nothing.
        _sanitizer.AllowedAttributes.Remove("type");
        _sanitizer.AllowedAttributes.Remove("disabled");
        _sanitizer.AllowedAttributes.Remove("checked");
        //
        // RemovingAttribute fires only for an attribute that is ALREADY slated for removal, and its
        // Cancel merely KEEPS it - so it can loosen but never tighten. That is exactly the
        // direction needed here: leave the three attributes globally disallowed, and cancel their
        // removal on <input> alone.
        //
        // "Loosen" is not the same as "harmless". RemoveReason has 8 values, and cancelling a
        // NotAllowedUrlValue resurrects a value the URL sanitiser had already rejected - a
        // cancel-everything handler yields <a href="javascript:alert(1)">. This handler is safe
        // ONLY because none of type/disabled/checked is in UriAttributes (action, background,
        // dynsrc, formaction, href, lowsrc, src), so it can never fire for a URL value.
        // DO NOT add a URI attribute to TaskListCheckboxAttributes: that set feeds both this
        // cancel and the PostProcessDom whitelist below, so one edit would widen both.
        //
        // ACCEPTED LOSS, decided not overlooked: because the cancel is scoped to <input>, a raw
        // <ol type="A"> now loses its marker style (rendered "A." before, "1." after) - `type` was
        // in the 95 defaults, so it used to survive on <ol>. Kept as a loss: markdown has no syntax
        // for <ol type>, it can only arrive as raw HTML, and restoring it would mean a second
        // per-tag exception in a mechanism whose whole point is a closed set. It is also milder
        // than the class/id losses this renderer already accepts (dead heading anchors, no
        // fenced-code highlighting). Pinned by OrderedListTypeAttribute_IsStripped_AcceptedLoss.
        _sanitizer.RemovingAttribute += (_, e) =>
        {
            if (e.Tag.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase)
                && TaskListCheckboxAttributes.Contains(e.Attribute.LocalName))
            {
                e.Cancel = true;
            }
        };

        // Closed-set guard on <input> itself (#340). Scoping the three attributes above is not
        // enough, because `value` is in HtmlSanitizer's DEFAULT allow-list: stored markdown could
        // render <input type="text" value="Enter password"> as a live, native-looking text box in
        // any description or comment (UI spoofing), and an <input> with no type at all still
        // renders as a text box. Denying known-bad attributes would be an open-ended list, so this
        // inverts it: an <input> survives only when it IS the disabled checkbox, and then keeps
        // only the three attributes that make it one. Everything else about an input is dropped.
        //
        // The whitelist is scoped by attribute NAME, not by value: <input type="checkbox" disabled
        // checked="anything"> keeps that arbitrary value. Harmless - checked is boolean, the value
        // is ignored by the browser, and AngleSharp escapes it on serialisation - but it means this
        // is not a value-level guard. `type` IS value-checked, in IsTaskListCheckbox below.
        //
        // Residual surface, in spec and deliberate: an attacker-authored
        // `<input type="checkbox" disabled>` still renders, because it is indistinguishable from
        // Markdig's own. So `[<input type=checkbox disabled> Approve](https://evil.example)` paints
        // a fake checkbox-and-label inside an arbitrary link. AC1 asks only that no LIVE form field
        // survive; a disabled checkbox is inert, so this is accepted rather than fixed.
        //
        // PostProcessDom is the tightening hook - it runs inside Sanitize() after the allow-list
        // pass and before serialisation, so there is no re-parse/re-serialise round trip.
        // (IsAllowedAttribute is private and non-virtual in 9.1.974, so subclassing is not an
        // option; measured against the installed assembly, not assumed.)
        _sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var input in e.Document.QuerySelectorAll("input").ToList())
            {
                if (!IsTaskListCheckbox(input))
                {
                    input.Remove();
                    continue;
                }

                foreach (var attribute in input.Attributes
                             .Where(a => !TaskListCheckboxAttributes.Contains(a.LocalName))
                             .ToList())
                {
                    input.RemoveAttribute(attribute.NamespaceUri, attribute.LocalName);
                }
            }
        };
    }

    /// <summary>
    /// True only for the shape Markdig emits for a task-list item: a checkbox that is disabled.
    /// Requiring <c>disabled</c> keeps an injected interactive checkbox out as well.
    /// </summary>
    private static bool IsTaskListCheckbox(IElement input) =>
        string.Equals(input.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase)
        && input.HasAttribute("disabled");

    public string RenderToSafeHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var html = Markdown.ToHtml(markdown, _pipeline);
        return _sanitizer.Sanitize(html);
    }
}
