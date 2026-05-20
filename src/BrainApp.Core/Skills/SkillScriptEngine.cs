using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;

namespace BrainApp.Core.Skills;

public class SkillScriptEngine
{
    private readonly SkillsSettings _settings;
    private readonly ConcurrentDictionary<string, CachedCompilation> _cache = new();

    public SkillScriptEngine(IOptions<SkillsSettings> settings)
    {
        _settings = settings.Value;
    }

    public SkillDefinition CompileFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var definition = new SkillDefinition
        {
            FileName = fileName,
            FilePath = filePath
        };

        if (!File.Exists(filePath))
        {
            definition.CompileError = "File not found";
            return definition;
        }

        var lastWrite = File.GetLastWriteTimeUtc(filePath);
        var cacheKey = $"{filePath}|{lastWrite.Ticks}";

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            ApplyCached(definition, cached);
            return definition;
        }

        try
        {
            var userCode = File.ReadAllText(filePath);
            var fullScript = SkillPreamble.BuildFullScript(userCode);
            var compiled = Compile(fullScript, fileName);

            if (compiled.Error != null)
            {
                definition.CompileError = compiled.Error;
                return definition;
            }

            var methods = DiscoverMethods(compiled.Assembly!);
            if (methods.Count == 0)
            {
                definition.CompileError = "No methods marked with [Skill] found";
                return definition;
            }

            var entry = new CachedCompilation
            {
                Assembly = compiled.Assembly!,
                Methods = methods,
                PrimaryType = methods[0].CompiledType
            };

            _cache[cacheKey] = entry;
            ApplyCached(definition, entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to compile skill file {File}", fileName);
            definition.CompileError = ex.Message;
        }

        return definition;
    }

    private static void ApplyCached(SkillDefinition definition, CachedCompilation cached)
    {
        definition.CompileError = null;
        definition.Methods = cached.Methods;
        definition.CompiledType = cached.PrimaryType;
    }

    private (Assembly? Assembly, string? Error) Compile(string source, string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: assemblyName);
        var references = GetMetadataReferences();

        var compilation = CSharpCompilation.Create(
            $"BrainAppSkill_{Path.GetFileNameWithoutExtension(assemblyName)}_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .Take(5);
            return (null, string.Join("; ", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
        return (assembly, null);
    }

    private static List<MetadataReference> GetMetadataReferences()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Use the runtime's trusted platform assemblies to ensure core types
        // like System.Attribute resolve correctly for user skill code.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    refs.Add(path);
            }
        }

        // Add app/dependency assemblies not covered by TPA.
        AddAssemblyPath(refs, typeof(SkillAttribute).Assembly);
        AddAssemblyPath(refs, typeof(HtmlAgilityPack.HtmlDocument).Assembly);

        return refs
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToList();
    }

    private static void AddAssemblyPath(HashSet<string> refs, Assembly assembly)
    {
        var location = assembly.Location;
        if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            refs.Add(location);
    }

    private static List<SkillMethodDefinition> DiscoverMethods(Assembly assembly)
    {
        var methods = new List<SkillMethodDefinition>();

        foreach (var type in assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == SkillPreamble.NamespaceName))
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<SkillAttribute>();
                if (attr == null) continue;

                var parameters = method.GetParameters()
                    .Select(p => new SkillParameterDefinition
                    {
                        Name = p.Name ?? "arg",
                        TypeName = SimplifyTypeName(p.ParameterType)
                    })
                    .ToList();

                methods.Add(new SkillMethodDefinition
                {
                    SkillName = attr.Name,
                    Description = attr.Description,
                    ClassName = type.Name,
                    MethodName = method.Name,
                    Parameters = parameters,
                    CompiledType = type,
                    CompiledMethod = method
                });
            }
        }

        return methods;
    }

    private static string SimplifyTypeName(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(double)) return "double";
        if (type == typeof(long)) return "long";
        return type.Name;
    }

    public void InvalidateCache() => _cache.Clear();

    private sealed class CachedCompilation
    {
        public Assembly Assembly { get; set; } = null!;
        public List<SkillMethodDefinition> Methods { get; set; } = new();
        public Type? PrimaryType { get; set; }
    }
}
