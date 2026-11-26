using System.Reflection;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Shared;
using KanbanFlow.Models;

namespace KanbanFlow.Tests;

public sealed class ComponentEnumParseTests
{
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
}
