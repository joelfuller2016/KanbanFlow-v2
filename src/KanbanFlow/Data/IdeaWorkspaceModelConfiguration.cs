using KanbanFlow.Models.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

public static class IdeaWorkspaceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdeaWorkspaceState>(e =>
        {
            e.ToTable("IdeaWorkspaceStates");
            e.HasKey(s => s.WorkspaceId);
            e.Property(s => s.WorkspaceId).HasMaxLength(64);
            e.Property(s => s.Kind).HasMaxLength(16);
            e.Property(s => s.Revision).IsConcurrencyToken();
            e.HasIndex(s => new { s.Kind, s.SourceId }).IsUnique();
            e.HasIndex(s => s.LinkedProjectId);
        });
        modelBuilder.Entity<IdeaWorkspaceReceipt>(e =>
        {
            e.ToTable("IdeaWorkspaceReceipts");
            e.HasKey(r => new { r.WorkspaceId, r.OperationId });
            e.Property(r => r.WorkspaceId).HasMaxLength(64);
            e.Property(r => r.OperationId).HasMaxLength(200);
            e.Property(r => r.RequestHash).HasMaxLength(64);
            e.Property(r => r.Actor).HasMaxLength(200);
        });
    }
}
