namespace BrainApp.Core.Skills;

public class SkillDefinition
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? CompileError { get; set; }
    public bool IsValid => string.IsNullOrEmpty(CompileError) && Methods.Count > 0;
    public List<SkillMethodDefinition> Methods { get; set; } = new();
    public Type? CompiledType { get; set; }
}

public class SkillMethodDefinition
{
    public string SkillName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string FullName => $"{ClassName}.{MethodName}";
    public List<SkillParameterDefinition> Parameters { get; set; } = new();
    public Type? CompiledType { get; set; }
    public System.Reflection.MethodInfo? CompiledMethod { get; set; }
}

public class SkillParameterDefinition
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
}

public class SkillInvocation
{
    public string SkillKey { get; set; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

public class SkillExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public class ProfileSkillEntry
{
    public string FileName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public SkillDefinition? Definition { get; set; }
}
