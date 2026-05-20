namespace BrainApp.Core.Skills;

public class SkillContext
{
    public string ProfileId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string AppDataFolder { get; init; } = string.Empty;
    public CancellationToken CancellationToken { get; init; }
}
