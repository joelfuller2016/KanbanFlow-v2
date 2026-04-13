using KanbanFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Feature> Features => Set<Feature>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<Requirement> Requirements => Set<Requirement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        });

        modelBuilder.Entity<Subtask>(e =>
        {
            e.HasMany(s => s.ChildSubtasks).WithOne(s => s.ParentSubtask).HasForeignKey(s => s.ParentSubtaskId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Requirement>(e =>
        {
            e.HasOne(r => r.Project).WithMany(p => p.Requirements).HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Feature).WithMany().HasForeignKey(r => r.FeatureId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.TaskItem).WithMany().HasForeignKey(r => r.TaskItemId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
