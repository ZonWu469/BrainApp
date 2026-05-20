using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;

namespace BrainApp.Core.Skills;

public class SkillExecutor
{
    private readonly SkillsSettings _settings;

    public SkillExecutor(IOptions<SkillsSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<SkillExecutionResult> ExecuteAsync(
        SkillMethodDefinition method,
        SkillInvocation invocation,
        SkillContext context,
        CancellationToken ct = default)
    {
        if (method.CompiledMethod == null || method.CompiledType == null)
        {
            return new SkillExecutionResult
            {
                Success = false,
                Error = "Skill is not compiled"
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ExecutionTimeoutSeconds));

        try
        {
            var result = await Task.Run(() => InvokeMethod(method, invocation, context), timeoutCts.Token);
            var output = TruncateResult(ConvertResultToString(result));
            return new SkillExecutionResult { Success = true, Output = output };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new SkillExecutionResult { Success = false, Error = "Skill execution timed out" };
        }
        catch (Exception ex)
        {
            var message = UnwrapException(ex).Message;
            Log.Warning(ex, "Skill execution failed: {Skill}", method.FullName);
            return new SkillExecutionResult { Success = false, Error = message };
        }
    }

    private object? InvokeMethod(SkillMethodDefinition method, SkillInvocation invocation, SkillContext context)
    {
        var instance = CreateInstance(method.CompiledType!, context);
        var args = BuildArguments(method, invocation);
        var returnValue = method.CompiledMethod!.Invoke(instance, args);

        if (returnValue is Task task)
        {
            task.GetAwaiter().GetResult();
            if (task.GetType().IsGenericType)
            {
                var resultProp = task.GetType().GetProperty("Result");
                return resultProp?.GetValue(task);
            }
            return null;
        }

        return returnValue;
    }

    private static object CreateInstance(Type type, SkillContext context)
    {
        var ctorWithContext = type.GetConstructor(new[] { typeof(SkillContext) });
        if (ctorWithContext != null)
            return ctorWithContext.Invoke(new object[] { context });

        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Cannot create instance of {type.Name}");
    }

    private static object?[] BuildArguments(SkillMethodDefinition method, SkillInvocation invocation)
    {
        var parameters = method.CompiledMethod!.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            object? value = null;

            if (invocation.Arguments.TryGetValue(param.Name!, out var raw))
                value = ConvertArgument(raw, param.ParameterType);
            else if (invocation.Arguments.Count == 1 && parameters.Length == 1)
                value = ConvertArgument(invocation.Arguments.Values.First(), param.ParameterType);

            if (value == null && param.HasDefaultValue)
                value = param.DefaultValue;

            if (value == null && param.ParameterType.IsValueType && Nullable.GetUnderlyingType(param.ParameterType) == null)
                throw new ArgumentException($"Missing required argument: {param.Name}");

            args[i] = value;
        }

        return args;
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value == null) return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is JsonElement je)
            value = SkillCallParserJsonElement.ToObject(je);

        if (underlying.IsInstanceOfType(value))
            return value;

        if (underlying == typeof(string))
            return value.ToString();

        if (underlying == typeof(int) && int.TryParse(value.ToString(), out var i)) return i;
        if (underlying == typeof(long) && long.TryParse(value.ToString(), out var l)) return l;
        if (underlying == typeof(bool) && bool.TryParse(value.ToString(), out var b)) return b;
        if (underlying == typeof(double) && double.TryParse(value.ToString(), out var d)) return d;

        if (value is string json && (underlying.IsClass || underlying.IsInterface) && underlying != typeof(string))
        {
            return JsonSerializer.Deserialize(json, underlying)
                ?? throw new InvalidOperationException($"Cannot deserialize argument to {underlying.Name}");
        }

        return Convert.ChangeType(value, underlying);
    }

    private static string ConvertResultToString(object? result) =>
        result switch
        {
            null => string.Empty,
            string s => s,
            _ => JsonSerializer.Serialize(result)
        };

    private string TruncateResult(string output)
    {
        if (output.Length <= _settings.MaxSkillResultChars)
            return output;
        return output[.._settings.MaxSkillResultChars] + "\n...(truncated)";
    }

    private static Exception UnwrapException(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException != null)
            return agg.InnerException;
        return ex;
    }
}

/// <summary>Helper for SkillExecutor argument conversion from JsonElement.</summary>
internal static class SkillCallParserJsonElement
{
    public static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
