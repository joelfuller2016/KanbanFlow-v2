using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? httpContext = null) : base(options)
    {
        var request = httpContext?.HttpContext?.Request;
        DefaultAuditActor = request?.Headers["X-KanbanFlow-Actor"].FirstOrDefault()?.Trim()
            is { Length: > 0 } actor ? actor : request?.Path.StartsWithSegments("/api") == true ? "api" : "System";
    }

    public string DefaultAuditActor { get; }

    public DbSet<WorkRequest> WorkRequests => Set<WorkRequest>();
    public DbSet<RequestMessage> RequestMessages => Set<RequestMessage>();
    public DbSet<BriefRevision> BriefRevisions => Set<BriefRevision>();
    public DbSet<PlanVersion> PlanVersions => Set<PlanVersion>();
    public DbSet<PlannedTask> PlannedTasks => Set<PlannedTask>();
    public DbSet<TaskEvidence> TaskEvidence => Set<TaskEvidence>();
    public DbSet<RequestResult> RequestResults => Set<RequestResult>();
    public DbSet<ProjectRepository> ProjectRepositories => Set<ProjectRepository>();
    public DbSet<WorkflowReceipt> WorkflowReceipts => Set<WorkflowReceipt>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Feature> Features => Set<Feature>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<AcceptanceCriterion> AcceptanceCriteria => Set<AcceptanceCriterion>();
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<Idea> Ideas => Set<Idea>();
    public DbSet<MemoryNode> MemoryNodes => Set<MemoryNode>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<MemoryRelation> MemoryRelations => Set<MemoryRelation>();
    public DbSet<ExternalWorkItemLink> ExternalWorkItemLinks => Set<ExternalWorkItemLink>();
    public DbSet<ExternalWriteOperation> ExternalWriteOperations => Set<ExternalWriteOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        WorkflowModelConfiguration.Configure(modelBuilder);
        modelBuilder.Entity<Project>(e =>
        {
            e.HasMany(p => p.Features).WithOne(f => f.Project).HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Feature>(e =>
        {
            e.HasMany(f => f.Tasks).WithOne(t => t.Feature).HasForeignKey(t => t.FeatureId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.ChildFeatures).WithOne(f => f.ParentFeature).HasForeignKey(f => f.ParentFeatureId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.HasMany(t => t.Subtasks).WithOne(s => s.TaskItem).HasForeignKey(s => s.TaskItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.AcceptanceCriteria).WithOne(c => c.TaskItem).HasForeignKey(c => c.TaskItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Dependencies).WithOne(d => d.TaskItem).HasForeignKey(d => d.TaskItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.BlockingDependencies).WithOne(d => d.DependsOnTaskItem).HasForeignKey(d => d.DependsOnTaskItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.ExternalLinks).WithOne(l => l.Task).HasForeignKey(l => l.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalWorkItemLink>(e =>
        {
            e.HasIndex(l => new { l.Provider, l.ExternalNodeId })
                .IsUnique()
                .HasFilter("\"ExternalNodeId\" IS NOT NULL");
            e.HasIndex(l => new { l.TaskId, l.Provider }).IsUnique();
            e.HasIndex(l => new { l.Provider, l.RepositoryExternalId, l.ExternalNumber })
                .IsUnique()
                .HasFilter("\"State\" = 0 AND \"ExternalNumber\" IS NOT NULL");
        });

        modelBuilder.Entity<ExternalWriteOperation>(e =>
        {
            e.HasIndex(o => o.OperationId).IsUnique();
            // One journal for the lifetime of a task/provider pair. Failed operations are resumed,
            // never replaced with a second marker that could create a duplicate remote issue.
            e.HasIndex(o => new { o.TaskId, o.Provider }).IsUnique();
        });

        modelBuilder.Entity<Subtask>(e =>
        {
            e.HasMany(s => s.ChildSubtasks).WithOne(s => s.ParentSubtask).HasForeignKey(s => s.ParentSubtaskId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskComment>(e =>
        {
            e.HasOne(c => c.TaskItem).WithMany(t => t.Comments).HasForeignKey(c => c.TaskItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Requirement>(e =>
        {
            e.HasOne(r => r.Project).WithMany(p => p.Requirements).HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Feature).WithMany().HasForeignKey(r => r.FeatureId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.TaskItem).WithMany().HasForeignKey(r => r.TaskItemId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TaskDependency>(e =>
        {
            e.HasIndex(d => new { d.TaskItemId, d.DependsOnTaskItemId }).IsUnique();
            e.HasOne(d => d.Project).WithMany(p => p.TaskDependencies).HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActivityEvent>(e =>
        {
            // Activity rows are an audit trail. Keep ProjectId as historical attribution,
            // but do not enforce a FK that would cascade-delete or block project deletion.
            e.Ignore(a => a.Project);
            e.HasIndex(a => new { a.ProjectId, a.CreatedAt });
            e.HasIndex(a => new { a.EntityType, a.EntityId, a.CreatedAt });
            e.HasIndex(a => new { a.TaskId, a.CreatedAt });
            e.HasIndex(a => new { a.RelatedTaskId, a.CreatedAt });
        });

        modelBuilder.Entity<Idea>(e =>
        {
            // Ideas are the pre-project inbox (#180). The promoted-project link is optional
            // and SetNull on delete, so removing a project never deletes its originating idea.
            e.HasOne(i => i.PromotedProject)
                .WithMany()
                .HasForeignKey(i => i.PromotedProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(i => new { i.Status, i.CreatedAt });
            // EF auto-indexes FK columns, but declare it explicitly so the model is
            // self-documenting and matches the EnsureIdeaTable patcher (IX_Ideas_PromotedProjectId).
            e.HasIndex(i => i.PromotedProjectId);
        });

        modelBuilder.Entity<MemoryNode>(e =>
        {
            // SupersededById is left a plain scalar (no self-FK) so a bi-temporal supersede
            // never risks a cascade on the prior row.
            // Primary recall path: filter by scope + scope key + type.
            e.HasIndex(m => new { m.Scope, m.ScopeKey, m.Type });
            // Walk the supersession chain / find replaced versions.
            e.HasIndex(m => m.SupersededById);
        });

        modelBuilder.Entity<Observation>(e =>
        {
            // Detail rows cascade with their parent memory node.
            e.HasOne(o => o.MemoryNode).WithMany().HasForeignKey(o => o.MemoryNodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryRelation>(e =>
        {
            // No-FK discriminator edges (FromType/FromId → ToType/ToId), like ActivityEvent's audit
            // rows: an edge can point at memory nodes or existing card ids without cascade coupling.
            // Index both endpoints so edges are queryable from either side.
            e.HasIndex(r => new { r.FromType, r.FromId });
            e.HasIndex(r => new { r.ToType, r.ToId });
        });
    }
}
