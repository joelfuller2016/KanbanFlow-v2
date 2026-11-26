using System.Reflection;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Shared;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace KanbanFlow.Tests;

public sealed class ComponentEnumParseTests
{
    [Theory]
    [InlineData("unavailable")]
    [InlineData("99")]
    public async Task InvalidInternalSelectionRendersAllIdeasWithoutAnError(string raw)
    {
        using var factory = new SqliteTestFactory();
        var ideas = new IdeaService(factory);
        var first = await ideas.CreateAsync(new IdeaCreate("First idea"), "test");
        var second = await ideas.CreateAsync(new IdeaCreate("Second idea"), "test");
        await ideas.UpdateAsync(second.Id, new IdeaUpdate(Status: IdeaStatus.Triaging,
            ExpectedWorkflowVersion: second.WorkflowVersion), "test");

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IIdeaService>(ideas)
            .AddSingleton<IJSRuntime, UnavailableJs>()
            .AddSingleton<IMarkdownRenderer, MarkdownRenderer>()
            .BuildServiceProvider();
        await using var renderer = new SeededIdeasRenderer(services, raw);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = renderer.BeginRenderingComponent(typeof(Ideas), ParameterView.Empty);
            await output.QuiescenceTask;
            return output.ToHtmlString();
        });

        Assert.Contains($"data-testid=\"idea-card-{first.Id}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-testid=\"idea-card-{second.Id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"idea-load-error\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unavailable")]
    [InlineData("9999999999999")]
    [InlineData("-1")]
    [InlineData("99")]
    public void UnrecognizedStatusLeavesIdeasUnfiltered(string? raw)
    {
        var page = new Ideas();
        SetRawSelection(page, "statusFilterRaw", raw);

        Assert.Null(ReadSelection(page, "StatusFilter"));
        Assert.Null(ReadSelection(page, "VisibleStatuses"));
        Assert.Equal(false, ReadSelection(page, "HasActiveFilter"));
    }

    [Theory]
    [InlineData("0", IdeaStatus.New)]
    [InlineData("1", IdeaStatus.Triaging)]
    [InlineData("2", IdeaStatus.Promoted)]
    [InlineData("3", IdeaStatus.Archived)]
    [InlineData("4", IdeaStatus.Rejected)]
    public void IdeasStatusFilterAcceptsDisplayedOrdinals(string raw, IdeaStatus expected)
    {
        var page = new Ideas();
        SetRawSelection(page, "statusFilterRaw", raw);

        Assert.Equal(expected, ReadSelection(page, "StatusFilter"));
        Assert.Equal(expected, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IdeaStatus>>(
            ReadSelection(page, "VisibleStatuses"))));
        Assert.Equal(true, ReadSelection(page, "HasActiveFilter"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("unavailable", null)]
    [InlineData("9999999999999", null)]
    [InlineData("-1", null)]
    [InlineData("99", null)]
    [InlineData("0", TaskPriority.Low)]
    [InlineData("1", TaskPriority.Medium)]
    [InlineData("2", TaskPriority.High)]
    [InlineData("3", TaskPriority.Critical)]
    public void IdeaModalUsesDefinedPriorityOrNone(string? raw, TaskPriority? expected)
    {
        var modal = new IdeaModal();
        SetRawSelection(modal, "priorityRaw", raw);

        Assert.Equal(expected, ReadSelection(modal, "SelectedPriority"));
    }

    // Exercise the same private properties that the Razor render and save paths use.
    private const BindingFlags InstanceMember = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void SetRawSelection(object component, string field, string? raw) =>
        component.GetType().GetField(field, InstanceMember)!.SetValue(component, raw);

    private static object? ReadSelection(object component, string property) =>
        component.GetType().GetProperty(property, InstanceMember)!.GetValue(component);

    private sealed class SeededIdeasRenderer(IServiceProvider services, string raw)
        : StaticHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode)
        {
            if (componentType != typeof(Ideas))
                throw new NotSupportedException($"Static Ideas tests cannot render {componentType.Name} in {renderMode.GetType().Name} mode.");

            // Render the actual route component and its children without starting a browser circuit.
            var component = componentActivator.CreateInstance(componentType);
            SetRawSelection(component, "statusFilterRaw", raw);
            return component;
        }
    }

    private sealed class UnavailableJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new InvalidOperationException("Static rendering cannot call JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new InvalidOperationException("Static rendering cannot call JavaScript.");
    }
}
