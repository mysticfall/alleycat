using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Runs a one-shot dynamic assembly loading probe when explicitly requested via command-line arguments.
/// </summary>
public partial class TestRuntimeRunner : Node
{
    private const string ProbeCommandArg = "--integration-probe";
    private const string RunFactCommandArg = "--integration-run-fact";
    private const string ProbeAssemblyArg = "--probe-assembly";
    private const string ProbeTypeArg = "--probe-type";
    private const string ProbeMethodArg = "--probe-method";
    private const string ProbeReadyPropertyName = "ReadyRan";
    private const string SuccessMarker = "ALLEYCAT_INTEGRATION_PROBE_SUCCESS";
    private const string FailureMarker = "ALLEYCAT_INTEGRATION_PROBE_FAILURE";
    private const string TestPassMarker = "ALLEYCAT_INTEGRATION_TEST_PASS";
    private const string TestFailMarker = "ALLEYCAT_INTEGRATION_TEST_FAIL";
    private const int ProbeReadyFrameLimit = 5;
    private const string TestResultMarkerPrefix = "ALLEYCAT_INTEGRATION_TEST_RESULT:";

    /// <summary>
    /// Starts the probe only when probe arguments are provided.
    /// </summary>
    public override void _Ready() => _ = RunCommandIfRequestedAsync();

    private async Task RunCommandIfRequestedAsync()
    {
        string[] args = OS.GetCmdlineUserArgs();

        if (args.Contains(RunFactCommandArg, StringComparer.Ordinal))
        {
            await RunFactIfRequestedAsync(args);
            return;
        }

        if (!args.Contains(ProbeCommandArg, StringComparer.Ordinal))
        {
            return;
        }

        await RunProbeIfRequestedAsync(args);
    }

    private async Task RunProbeIfRequestedAsync(IReadOnlyList<string> args)
    {
        string? assemblyPath = GetArgumentValue(args, ProbeAssemblyArg);
        string? probeTypeName = GetArgumentValue(args, ProbeTypeArg);

        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(probeTypeName))
        {
            FailProbeAndQuit("Probe arguments were missing or empty.");
            return;
        }

        try
        {
            var godotLoadContext = AssemblyLoadContext.GetLoadContext(typeof(Node).Assembly);
            if (godotLoadContext is null)
            {
                FailProbeAndQuit("Could not resolve Godot assembly load context.");
                return;
            }

            if (!TryGetLoadTarget(assemblyPath, out string loadTarget, out AssemblyDependencyResolver? dependencyResolver))
            {
                FailProbeAndQuit("Probe assembly path was missing or invalid.");
                return;
            }

            Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
            {
                if (AssemblyName.ReferenceMatchesDefinition(typeof(Node).Assembly.GetName(), assemblyName))
                {
                    return typeof(Node).Assembly;
                }

                string? dependencyPath = dependencyResolver!.ResolveAssemblyToPath(assemblyName);
                return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
            }

            godotLoadContext.Resolving += OnResolving;
            try
            {
                Assembly assembly = godotLoadContext.LoadFromAssemblyPath(loadTarget);
                Type? probeType = assembly.GetType(probeTypeName, throwOnError: false);
                if (probeType is null)
                {
                    FailProbeAndQuit($"Probe type '{probeTypeName}' was not found in '{loadTarget}'.");
                    return;
                }

                if (!typeof(Node).IsAssignableFrom(probeType))
                {
                    FailProbeAndQuit($"Probe type '{probeTypeName}' does not inherit from Godot.Node.");
                    return;
                }

                if (Activator.CreateInstance(probeType) is not Node probeNode)
                {
                    FailProbeAndQuit($"Unable to instantiate probe node '{probeTypeName}'.");
                    return;
                }

                probeNode.RequestReady();
                AddChild(probeNode);

                (bool ready, string readyFlagError) = await WaitForReadyFlagAsync(probeType);
                if (!ready && !TryInvokeReadyCallback(probeNode, probeType, out readyFlagError))
                {
                    FailProbeAndQuit(readyFlagError);
                    return;
                }

                GD.Print(SuccessMarker);
                GetTree().Quit(0);
            }
            finally
            {
                godotLoadContext.Resolving -= OnResolving;
            }
        }
        catch (Exception ex)
        {
            FailProbeAndQuit($"Unexpected probe error: {ex}");
        }
    }

    private async Task RunFactIfRequestedAsync(IReadOnlyList<string> args)
    {
        string? assemblyPath = GetArgumentValue(args, ProbeAssemblyArg);
        string? typeName = GetArgumentValue(args, ProbeTypeArg);
        string? methodName = GetArgumentValue(args, ProbeMethodArg);

        if (string.IsNullOrWhiteSpace(assemblyPath)
            || string.IsNullOrWhiteSpace(typeName)
            || string.IsNullOrWhiteSpace(methodName))
        {
            ErrorTestAndQuit("Run-fact arguments were missing or empty.");
            return;
        }

        try
        {
            var godotLoadContext = AssemblyLoadContext.GetLoadContext(typeof(Node).Assembly);
            if (godotLoadContext is null)
            {
                ErrorTestAndQuit("Could not resolve Godot assembly load context.");
                return;
            }

            if (!TryGetLoadTarget(assemblyPath, out string loadTarget, out AssemblyDependencyResolver? dependencyResolver))
            {
                ErrorTestAndQuit("Run-fact assembly path was missing or invalid.");
                return;
            }

            Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
            {
                if (AssemblyName.ReferenceMatchesDefinition(typeof(Node).Assembly.GetName(), assemblyName))
                {
                    return typeof(Node).Assembly;
                }

                string? dependencyPath = dependencyResolver!.ResolveAssemblyToPath(assemblyName);
                return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
            }

            godotLoadContext.Resolving += OnResolving;
            try
            {
                Assembly assembly = godotLoadContext.LoadFromAssemblyPath(loadTarget);
                Type? type = assembly.GetType(typeName, throwOnError: false);
                if (type is null)
                {
                    ErrorTestAndQuit($"Type '{typeName}' was not found in '{loadTarget}'.");
                    return;
                }

                MethodInfo? method = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                if (method is null)
                {
                    ErrorTestAndQuit($"Method '{typeName}.{methodName}()' was not found.");
                    return;
                }

                PerTestLifecycleExecutionResult executionResult = await PerTestLifecycleExecutor.ExecuteAsync(method);
                if (!executionResult.Passed)
                {
                    (string message, string? stackTrace) = PerTestLifecycleExecutor.BuildFailureDiagnostics(executionResult);
                    FailTestAndQuit(message, stackTrace);
                    return;
                }

                GD.Print(TestPassMarker);
                EmitStructuredRunFactResult("passed", null, null);
                GetTree().Quit(0);
            }
            finally
            {
                godotLoadContext.Resolving -= OnResolving;
            }
        }
        catch (Exception ex)
        {
            ErrorTestAndQuit($"Unexpected run-fact error: {ex.Message}", ex.StackTrace);
        }
    }

    private static bool TryGetLoadTarget(string assemblyPath,
        out string absoluteAssemblyPath,
        out AssemblyDependencyResolver? dependencyResolver)
    {
        absoluteAssemblyPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(absoluteAssemblyPath))
        {
            dependencyResolver = null;
            return false;
        }

        dependencyResolver = new AssemblyDependencyResolver(absoluteAssemblyPath);

        return true;
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string argumentName)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, argumentName, StringComparison.Ordinal))
            {
                return index + 1 < args.Count ? args[index + 1] : null;
            }

            string prefix = $"{argumentName}=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private async Task<(bool Ready, string Error)> WaitForReadyFlagAsync(Type probeType)
    {
        string readyFlagError = string.Empty;
        for (int frame = 0; frame < ProbeReadyFrameLimit; frame++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (TryReadReadyFlag(probeType, out readyFlagError))
            {
                return (true, string.Empty);
            }
        }

        return (false, readyFlagError);
    }

    private static bool TryInvokeReadyCallback(Node probeNode, Type probeType, out string error)
    {
        MethodInfo? readyMethod = probeType.GetMethod(
            nameof(_Ready),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (readyMethod is null)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution and does not expose a public _Ready callback.";
            return false;
        }

        try
        {
            _ = readyMethod.Invoke(probeNode, null);
        }
        catch (Exception ex)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution and invoking _Ready failed: {ex.Message}";
            return false;
        }

        return TryReadReadyFlag(probeType, out error);
    }

    private static bool TryReadReadyFlag(Type probeType, out string error)
    {
        PropertyInfo? property = probeType.GetProperty(ProbeReadyPropertyName, BindingFlags.Public | BindingFlags.Static);
        if (property is null)
        {
            error = $"Probe type '{probeType.FullName}' does not define public static bool {ProbeReadyPropertyName}.";
            return false;
        }

        if (property.PropertyType != typeof(bool))
        {
            error = $"Probe property '{probeType.FullName}.{ProbeReadyPropertyName}' is not a bool.";
            return false;
        }

        object? value = property.GetValue(null);
        if (value is not bool readyRan)
        {
            error = $"Probe property '{probeType.FullName}.{ProbeReadyPropertyName}' returned a non-boolean value.";
            return false;
        }

        if (!readyRan)
        {
            error = $"Probe node '{probeType.FullName}' did not report ready execution.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void FailProbeAndQuit(string details)
    {
        GD.PrintErr($"{FailureMarker}: {details}");
        GetTree().Quit(1);
    }

    private void FailTestAndQuit(string details, string? stackTrace = null)
        => FailTestAndQuit(details, stackTrace, "failed");

    private void ErrorTestAndQuit(string details, string? stackTrace = null)
        => FailTestAndQuit(details, stackTrace, "error");

    private void FailTestAndQuit(string details, string? stackTrace, string structuredOutcome)
    {
        string renderedDetails = string.IsNullOrWhiteSpace(stackTrace)
            ? details
            : $"{details}{System.Environment.NewLine}{stackTrace}";

        GD.PrintErr($"{TestFailMarker}: {renderedDetails}");
        EmitStructuredRunFactResult(structuredOutcome, details, stackTrace);
        GetTree().Quit(1);
    }

    private static void EmitStructuredRunFactResult(string outcome, string? message, string? stack)
    {
        var payload = new RunFactResultPayload(outcome, message, stack);
        string json = JsonSerializer.Serialize(payload);
        GD.Print($"{TestResultMarkerPrefix}{json}");
    }

    private sealed record RunFactResultPayload(string Outcome, string? Message, string? Stack);

}
