using KanbanFlow.Models.Workflows;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

public static class WorkflowModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        // Workflow IDs are assigned before graph composition. They are never store-generated;
        // otherwise EF may treat a new child with a non-default Guid as an existing row.
        modelBuilder.Entity<WorkRequest>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<RequestMessage>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<BriefRevision>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<PlanVersion>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<PlannedTask>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<TaskEvidence>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<RequestResult>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<WorkRequest>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Idea).WithOne().HasForeignKey<WorkRequest>(x => x.IdeaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<KanbanFlow.Models.Feature>().WithMany().HasForeignKey(x => x.FeatureId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Messages).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Briefs).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Plans).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.WorkItems).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Results).WithOne(x => x.Request).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RequestMessage>().HasIndex(x => new { x.RequestId, x.Kind })
            .IsUnique().HasFilter("Kind = 'original'");
        modelBuilder.Entity<BriefRevision>().HasIndex(x => new { x.RequestId, x.Number }).IsUnique();
        modelBuilder.Entity<PlanVersion>(e =>
        {
            e.HasIndex(x => new { x.RequestId, x.Number }).IsUnique();
            e.HasOne(x => x.Brief).WithMany().HasForeignKey(x => x.BriefRevisionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PlannedTask>(e =>
        {
            e.HasIndex(x => new { x.RequestId, x.DraftId }).IsUnique();
            e.HasIndex(x => x.TaskItemId).IsUnique();
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Evidence).WithOne(x => x.WorkItem).HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ProjectRepository>(e =>
        {
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.LocalPath }).IsUnique();
        });
        modelBuilder.Entity<WorkflowReceipt>().HasIndex(x => new { x.Actor, x.Operation, x.Key }).IsUnique();
    }
}
