namespace KanbanFlow.Services;

public interface IMarkdownRenderer
{
    string RenderToSafeHtml(string? markdown);
}
