using System.Globalization;
using AlleyCat.Templating;
using Fluid;
using Xunit;

namespace AlleyCat.Tests.Templating;

/// <summary>
/// Golden-baseline harness for TMPL-001: proves that the migrated Fluid engine reproduces the committed
/// Handlebars-era snapshots byte-for-byte, and can re-capture snapshots during migration windows.
/// </summary>
/// <remarks>
/// The baselines were captured against Handlebars.Net 2.4.3 immediately before the Fluid migration; they are
/// frozen reference history and must never be regenerated. Capture mode is gated behind the
/// <see cref="CaptureEnvironmentVariable"/> environment variable, refuses to overwrite existing snapshots, and
/// exists only so future deliberate re-baselinings have a single entry point.
/// </remarks>
public sealed class TemplatingBaselineTests
{
    /// <summary>Set to <c>1</c> to enable migration-time baseline capture.</summary>
    public const string CaptureEnvironmentVariable = "ALLEYCAT_TEMPLATING_CAPTURE_BASELINES";

    private const string CaptureEngineMoniker = "Handlebars.Net 2.4.3";

    private const string GoldenEngineMoniker = "Fluid.Core 2.40.0";

    /// <summary>
    /// Captures one snapshot per scenario. Only runs when the capture environment variable equals <c>1</c>;
    /// otherwise the fact passes without touching the repository.
    /// </summary>
    [Fact]
    public async Task CaptureBaselinesWritesMissingSnapshotsOnly()
    {
        if (Environment.GetEnvironmentVariable(CaptureEnvironmentVariable) != "1")
        {
            return;
        }

        string directory = BaselineDirectory();
        _ = Directory.CreateDirectory(directory);

        foreach (TemplatingBaselineScenario scenario in TemplatingBaselineScenarios.All)
        {
            string successPath = SuccessSnapshotPath(scenario);
            string failurePath = FailureSnapshotPath(scenario);
            if (File.Exists(successPath) || File.Exists(failurePath))
            {
                throw new InvalidOperationException(
                    $"Baseline snapshot for '{scenario.Name}' already exists and must not be overwritten.");
            }

            try
            {
                string output = await RenderScenarioAsync(scenario);
                if (scenario.ExpectFailure)
                {
                    throw new InvalidOperationException(
                        $"Baseline scenario '{scenario.Name}' was expected to fail but rendered successfully.");
                }

                File.WriteAllText(successPath, output);
            }
            catch (Exception exception) when (scenario.ExpectFailure)
            {
                File.WriteAllText(failurePath, FormatFailureEnvelope(exception, CaptureEngineMoniker));
            }
        }
    }

    /// <summary>
    /// Guards catalogue drift: every defined scenario must have a committed baseline snapshot.
    /// </summary>
    [Fact]
    public void CommittedBaselinesCoverEveryScenario()
    {
        foreach (TemplatingBaselineScenario scenario in TemplatingBaselineScenarios.All)
        {
            Assert.True(
                File.Exists(SuccessSnapshotPath(scenario)) || File.Exists(FailureSnapshotPath(scenario)),
                $"Committed baseline snapshot for scenario '{scenario.Name}' is missing.");
        }
    }

    /// <summary>
    /// Every successful baseline snapshot is reproduced byte-for-byte by the Fluid engine through its Liquid
    /// translation of the authored source.
    /// </summary>
    [Fact]
    public async Task GoldenBaselinesMatchCommittedSnapshots()
    {
        foreach (TemplatingBaselineScenario scenario in TemplatingBaselineScenarios.All)
        {
            if (File.Exists(FailureSnapshotPath(scenario)))
            {
                // Failure-mode scenarios are covered by their dedicated golden fact below.
                continue;
            }

            string expected = await File.ReadAllTextAsync(SuccessSnapshotPath(scenario));
            string actual = await RenderGoldenScenarioAsync(scenario);

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"Baseline mismatch for '{scenario.Name}'."
                + $"{Environment.NewLine}Expected ({expected.Length} chars): {Describe(expected)}"
                + $"{Environment.NewLine}Actual   ({actual.Length} chars): {Describe(actual)}");
        }
    }

    /// <summary>
    /// The broken-template golden expectation reproduces the Fluid engine's parse failure byte-for-byte while
    /// the captured Handlebars failure remains as frozen reference history.
    /// </summary>
    [Fact]
    public async Task BrokenTemplateGoldenExpectationMatchesFluidFailure()
    {
        TemplatingBaselineScenario brokenTemplate = TemplatingBaselineScenarios.All
            .Single(scenario => File.Exists(FailureSnapshotPath(scenario)));

        Exception exception = await Record.ExceptionAsync(() => RenderGoldenScenarioAsync(brokenTemplate));

        Assert.NotNull(exception);
        _ = Assert.IsType<ParseException>(exception);
        Assert.Equal(
            FormatFailureEnvelope(exception, GoldenEngineMoniker),
            await File.ReadAllTextAsync(GoldenFailureSnapshotPath(brokenTemplate)));
    }

    internal static string BaselineDirectory()
        => RepositoryPath.Get("tests", "src", "Templating", "baselines");

    private static string SuccessSnapshotPath(TemplatingBaselineScenario scenario)
        => Path.Combine(BaselineDirectory(), $"{scenario.Name}.txt");

    private static string FailureSnapshotPath(TemplatingBaselineScenario scenario)
        => Path.Combine(BaselineDirectory(), $"{scenario.Name}.failure.handlebars.txt");

    private static string GoldenFailureSnapshotPath(TemplatingBaselineScenario scenario)
        => Path.Combine(BaselineDirectory(), $"{scenario.Name}.failure.fluid.txt");

    private static Task<string> RenderScenarioAsync(TemplatingBaselineScenario scenario)
        => RenderCoreAsync(scenario.CaptureSource, scenario);

    private static Task<string> RenderGoldenScenarioAsync(TemplatingBaselineScenario scenario)
        => RenderCoreAsync(scenario.FluidSource, scenario);

    private static async Task<string> RenderCoreAsync(
        string source,
        TemplatingBaselineScenario scenario)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = ResolveCulture(scenario.CultureName);

        try
        {
            FluidTemplateCompilerEngine compiler = new();
            if (scenario.Partials is not null)
            {
                foreach (KeyValuePair<string, string> partial in scenario.Partials)
                {
                    compiler.RegisterPartial(partial.Key, partial.Value);
                }
            }

            ITemplate template = compiler.Compile(source);
            return await template.RenderAsync(scenario.ContextFactory());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static CultureInfo ResolveCulture(string? cultureName)
        => string.IsNullOrWhiteSpace(cultureName)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(cultureName);

    private static string Describe(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string FormatFailureEnvelope(Exception exception, string engineMoniker)
    {
        string escapedMessage = exception.Message
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return "# alleycat templating baseline failure\n"
            + $"# engine: {engineMoniker}\n"
            + $"# exception: {exception.GetType().FullName}\n"
            + $"# message: {escapedMessage}\n";
    }
}
