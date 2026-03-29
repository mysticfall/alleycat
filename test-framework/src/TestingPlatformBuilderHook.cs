using System.Reflection;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.CommandLine;

namespace AlleyCat.TestFramework;

/// <summary>
/// Registers the AlleyCat integration test framework with Microsoft Testing Platform.
/// </summary>
public static class TestingPlatformBuilderHook
{
    /// <summary>
    /// Adds the AlleyCat test framework extension to the builder.
    /// </summary>
    /// <param name="builder">The test application builder.</param>
    /// <param name="args">Command-line arguments passed to the test host.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style", "IDE0060:Remove unused parameter", Justification = "Required by MTP hook contract.")]
    public static void AddExtensions(ITestApplicationBuilder builder, string[] args)
    {
        Assembly testAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

#pragma warning disable TPEXP // MTP previews AddProvider as experimental.
        builder.CommandLine.AddProvider(_ => new GodotTestCommandLineOptionsProvider());
#pragma warning restore TPEXP

        _ = builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities([]),
            (_, serviceProvider) => new GodotTestFramework(
                testAssembly,
                GetCliSelector(serviceProvider)));
    }

    private static GodotCliTestSelector GetCliSelector(IServiceProvider serviceProvider)
        => serviceProvider.GetService(typeof(ICommandLineOptions)) is ICommandLineOptions commandLineOptions
            ? GodotTestCommandLineOptions.Parse(commandLineOptions)
            : GodotCliTestSelector.None;
}
