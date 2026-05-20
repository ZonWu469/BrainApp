namespace BrainApp.Core.Skills;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkillAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; set; } = string.Empty;

    public SkillAttribute(string name)
    {
        Name = name;
    }
}
