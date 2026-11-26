namespace KanbanFlow.Components.Shared;

public sealed record ProjectPanelPreference(string Dock, int? Width, bool Collapsed);

public sealed record ProjectPanelLayout(ProjectPanelPreference Outline, ProjectPanelPreference Details)
{
    public const string ModulePath = "./js/project-panel-layout.js";

    public static ProjectPanelLayout Default { get; } =
        new(new("left", null, false), new("right", null, false));
}
