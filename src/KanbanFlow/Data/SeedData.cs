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
        };

        db.Projects.Add(project);
        db.SaveChanges();

        // Top-level features
        var userMgmt = new Feature { ProjectId = project.Id, Name = "User Management", SortOrder = 0 };
        var dashboard = new Feature { ProjectId = project.Id, Name = "Dashboard", SortOrder = 1 };
        db.Features.AddRange(userMgmt, dashboard);
        db.SaveChanges();

        // Sub-feature under User Management
        var authFeature = new Feature { ProjectId = project.Id, ParentFeatureId = userMgmt.Id, Name = "Authentication", SortOrder = 0 };
        var profileFeature = new Feature { ProjectId = project.Id, ParentFeatureId = userMgmt.Id, Name = "User Profile", SortOrder = 1 };
        db.Features.AddRange(authFeature, profileFeature);
        db.SaveChanges();

        // Tasks under Authentication sub-feature
        var loginTask = new TaskItem { FeatureId = authFeature.Id, Name = "Login page", Status = WorkStatus.Done, Priority = TaskPriority.High, SortOrder = 0, CompletedAt = DateTime.UtcNow };
        var regTask = new TaskItem { FeatureId = authFeature.Id, Name = "Registration form", Status = WorkStatus.InProgress, Priority = TaskPriority.High, SortOrder = 1 };
        // Tasks under User Profile sub-feature
        var pwdTask = new TaskItem { FeatureId = profileFeature.Id, Name = "Password reset", Status = WorkStatus.Todo, Priority = TaskPriority.Medium, SortOrder = 0 };
        // Tasks under Dashboard (top-level)
        var kpiTask = new TaskItem { FeatureId = dashboard.Id, Name = "KPI cards", Status = WorkStatus.Backlog, Priority = TaskPriority.Medium, SortOrder = 0 };
        var feedTask = new TaskItem { FeatureId = dashboard.Id, Name = "Activity feed", Status = WorkStatus.Backlog, Priority = TaskPriority.Low, SortOrder = 1 };

        db.Tasks.AddRange(loginTask, regTask, pwdTask, kpiTask, feedTask);
        db.SaveChanges();

        // Nested subtasks on the Registration form task
        var designSubtask = new Subtask { TaskItemId = regTask.Id, Text = "Design form layout", SortOrder = 0 };
        var validationSubtask = new Subtask { TaskItemId = regTask.Id, Text = "Add validation", SortOrder = 1 };
        db.Subtasks.AddRange(designSubtask, validationSubtask);
        db.SaveChanges();

        // Sub-subtask under "Add validation"
        var emailValidation = new Subtask { TaskItemId = regTask.Id, ParentSubtaskId = validationSubtask.Id, Text = "Email format check", SortOrder = 0 };
        var pwdValidation = new Subtask { TaskItemId = regTask.Id, ParentSubtaskId = validationSubtask.Id, Text = "Password strength check", SortOrder = 1 };
        db.Subtasks.AddRange(emailValidation, pwdValidation);
        db.SaveChanges();

        // Set project readme
        project.Readme = "# Sample Project\n\nThis is a demo project for KanbanFlow.\n\n## Getting Started\n\n1. Create features to organize work\n2. Add tasks to features\n3. Track progress on the kanban board";
        db.SaveChanges();

        // Requirements
        var req1 = new Requirement { ProjectId = project.Id, Text = "User can log in with email and password", Priority = RequirementPriority.P0, Status = RequirementStatus.Done, FeatureId = authFeature.Id, SortOrder = 0 };
        var req2 = new Requirement { ProjectId = project.Id, Text = "Dashboard shows KPI metrics", Priority = RequirementPriority.P1, Status = RequirementStatus.Open, FeatureId = dashboard.Id, SortOrder = 1 };
        var req3 = new Requirement { ProjectId = project.Id, Text = "Password must meet complexity requirements", Priority = RequirementPriority.P0, Status = RequirementStatus.InProgress, FeatureId = profileFeature.Id, SortOrder = 2 };
        db.Requirements.AddRange(req1, req2, req3);
        db.SaveChanges();
    }
}
