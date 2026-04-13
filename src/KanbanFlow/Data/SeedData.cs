using KanbanFlow.Models;

namespace KanbanFlow.Data;

public static class SeedData
{
    public static void Initialize(AppDbContext db)
    {
        if (db.Projects.Any()) return;

        var project = new Project
        {
            Name = "Sample Project",
            Description = "A demo project to get started with KanbanFlow.",
            Goals = "- Launch MVP\n- Get user feedback\n- Iterate on design",
            Features =
            [
                new Feature
                {
                    Name = "User Management",
                    SortOrder = 0,
                    Tasks =
                    [
                        new TaskItem { Name = "Login page", Status = WorkStatus.Done, Priority = TaskPriority.High, SortOrder = 0, CompletedAt = DateTime.UtcNow },
                        new TaskItem { Name = "Registration form", Status = WorkStatus.InProgress, Priority = TaskPriority.High, SortOrder = 1 },
                        new TaskItem { Name = "Password reset", Status = WorkStatus.Todo, Priority = TaskPriority.Medium, SortOrder = 2 },
                    ]
                },
                new Feature
                {
                    Name = "Dashboard",
                    SortOrder = 1,
                    Tasks =
                    [
                        new TaskItem { Name = "KPI cards", Status = WorkStatus.Backlog, Priority = TaskPriority.Medium, SortOrder = 0 },
                        new TaskItem { Name = "Activity feed", Status = WorkStatus.Backlog, Priority = TaskPriority.Low, SortOrder = 1 },
                    ]
                }
            ]
        };

        db.Projects.Add(project);
        db.SaveChanges();
    }
}
