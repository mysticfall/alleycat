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
            (_, serviceProvider) =>
            {
                (GodotCliTestSelector selector, bool headlessOverride) = GetCliSelector(serviceProvider);
                return new GodotTestFramework(
                    testAssembly,
                    selector,
                    processFactory: null,
                    headlessOverride);
            });
    }

    private static (GodotCliTestSelector Selector, bool HeadlessOverride) GetCliSelector(
        IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(ICommandLineOptions)) is not ICommandLineOptions commandLineOptions)
        {
            return (GodotCliTestSelector.None, false);
        }

        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);
        bool headlessOverride = GodotTestCommandLineOptions.IsHeadless(commandLineOptions);

        return (selector, headlessOverride);
    }
}
