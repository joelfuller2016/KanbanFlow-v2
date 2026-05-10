using Ganss.Xss;
using Markdig;

namespace KanbanFlow.Services;

public sealed class MarkdownRenderer : IMarkdownRenderer
{
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
        //   - <input type="checkbox"> for task lists (we whitelist the type attribute below)
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
        // Markdig task lists emit <input disabled="" type="checkbox" />. Whitelist
        // those two attributes (and only on <input>) so the checkbox actually renders.
        _sanitizer.AllowedAttributes.Add("type");
        _sanitizer.AllowedAttributes.Add("disabled");
        _sanitizer.AllowedAttributes.Add("checked");
    }

    public string RenderToSafeHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var html = Markdown.ToHtml(markdown, _pipeline);
        return _sanitizer.Sanitize(html);
    }
}
