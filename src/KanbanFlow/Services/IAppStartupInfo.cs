namespace KanbanFlow.Services;

/// <summary>
/// Captures process-level startup metadata at app boot. Registered as a singleton so
/// every consumer sees the same start time — fixes the bug where /api/admin/health
/// returned the *request* time and Settings.razor returned the *component-init* time
/// (issue raised by review on PRs #99 + #104).
/// </summary>
public interface IAppStartupInfo
{
    DateTime StartedAtUtc { get; }
}

public sealed class AppStartupInfo : IAppStartupInfo
{
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
}
