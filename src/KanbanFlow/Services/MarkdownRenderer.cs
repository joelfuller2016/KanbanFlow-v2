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
        foreach (var tag in new[]
        {
            "p", "h1", "h2", "h3", "h4", "h5", "h6",
            "a", "ul", "ol", "li",
            "code", "pre", "blockquote",
            "strong", "em",
            "table", "thead", "tbody", "tr", "td", "th",
            "hr", "br", "img"
        })
        {
            _sanitizer.AllowedTags.Add(tag);
        }
    }

    public string RenderToSafeHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var html = Markdown.ToHtml(markdown, _pipeline);
        return _sanitizer.Sanitize(html);
    }
}
