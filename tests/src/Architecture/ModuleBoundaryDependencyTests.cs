using System.Reflection;
using System.Text.RegularExpressions;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Tests.Architecture;

/// <summary>
/// Guards the dependency boundaries restored by the module-boundary refactor so they cannot silently regress:
/// Core stays free of feature diagnostics, Prompting owns projection identity without runner session-protocol
/// types, the session runner and the common tool session stay generic, and generic Mind ingestion carries no
/// speech modality branch (CORE-007 TR-19/20/21, AI-001 TR-49/50, AI-002 TR-19/23/62/63, AI-003 TR-35/36).
/// The Speech-stays-Mind-free guard is kept in <see cref="PerceptSensingDependencyTests" />.
/// </summary>
public sealed class ModuleBoundaryDependencyTests
{
    /// <summary>
    /// Core routes generic logging and notifications only (CORE-007 TR-19/20): production sources under
    /// <c>game/src/Core/</c> must not name STT or speech at all. Matching rule: case-insensitive substring
    /// <c>stt</c> or <c>speech</c> anywhere on a line of any <c>*.cs</c> file in the directory tree —
    /// identifiers, string literals, category names, and comments all count, because feature concepts must not
    /// appear in the feature-free core in any form.
    /// </summary>
    [Fact]
    public void ProductionCore_DoesNotNameSttOrSpeechDiagnostics()
    {
        AssertSourcesContainNoTokens(
            "Core must stay free of STT and speech feature diagnostics (CORE-007 TR-19/20).",
            RequireSourceTree("game", "src", "Core"),
            StringComparison.OrdinalIgnoreCase,
            "stt",
            "speech");
    }

    /// <summary>
    /// Prompting owns the projected event identity and must not expose runner session-protocol types (AI-003
    /// TR-35/36): projection output carries the Prompting-owned <c>SpeechGroupCorrelation</c> only. Matching
    /// rule: ordinal substring for the runner-protocol continuation identity across every <c>*.cs</c> file under
    /// <c>game/src/Mind/AI/Prompting/</c>;
    /// unqualified use through parent-namespace resolution is caught the same as imported or qualified use.
    /// </summary>
    [Fact]
    public void Prompting_ExposesNoRunnerSessionProtocolTypes()
    {
        AssertSourcesContainNoTokens(
            "Prompting must not expose runner session-protocol types (AI-003 TR-35/36).",
            RequireSourceTree("game", "src", "Mind", "AI", "Prompting"),
            StringComparison.Ordinal,
            "SpeechContinuationKey",
            "AgentSessionRunner");
    }

    /// <summary>
    /// The session runner must operate only on generic phase concepts, with per-function phase policy registered
    /// at tool composition (AI-002 TR-62): it must not reference, match, or name any concrete production tool,
    /// function name, or tool type. Matching rule on <c>AgentSessionRunner.cs</c>: ordinal substring for the
    /// tool-namespace import <c>Mind.AI.Tool</c> and the tool type-name roots <c>SpeechTool</c>, <c>WaitTool</c>,
    /// <c>HistoryTool</c>, <c>AgentTool</c>, and <c>ProductionToolName</c>, plus the case-insensitive
    /// word-boundary pattern <c>speak</c> for concrete function-name matching — a comment naming the function
    /// counts as naming it, so reword generically.
    /// </summary>
    [Fact]
    public void AgentSessionRunner_ReferencesNoConcreteProductionTool()
    {
        const string Rule = "The session runner must stay generic and reference no concrete production tool (AI-002 TR-62).";
        string runner = RequireSourceFile("game", "src", "Mind", "AI", "AgentSessionRunner.cs");

        AssertSourcesContainNoTokens(
            Rule,
            [runner],
            StringComparison.Ordinal,
            "Mind.AI.Tool",
            "SpeechTool",
            "WaitTool",
            "HistoryTool",
            "AgentTool",
            "ProductionToolName");

        AssertSourcesContainNoPatterns(
            Rule,
            [runner],
            [new Regex(@"\bspeak\b", RegexOptions.IgnoreCase)]);
    }

    /// <summary>
    /// The common tool session binds only the shared tuple (Context, Mind, Clock) and carries no
    /// feature services (AI-002 TR-19/23): speech-admission arbitration and wait-delivery acknowledgement bind
    /// typed to their concrete tools at the AgenticMind composition boundary instead. Matching rule: reflection
    /// shape assertion — the exact public instance property set and constructor signature of
    /// <see cref="AgentToolSession" />, with public fields and events required empty, so any admission,
    /// delivery, or other feature member fails the guard whatever it is called.
    /// </summary>
    [Fact]
    public void AgentToolSession_BindsOnlyCommonSessionServices()
    {
        Type session = typeof(AgentToolSession);

        Dictionary<string, Type> expectedServices = new()
        {
            [nameof(AgentToolSession.Context)] = typeof(ScenarioContext),
            [nameof(AgentToolSession.Mind)] = typeof(MindBase),
            [nameof(AgentToolSession.Clock)] = typeof(IGameClock),
        };

        string[] actualPropertyNames = [.. session.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];
        Assert.Equal(["Clock", "Context", "Mind"], actualPropertyNames);

        foreach ((string name, Type propertyType) in expectedServices)
        {
            PropertyInfo? property = session.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Equal(propertyType, property.PropertyType);
        }

        Assert.Empty(session.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(session.GetEvents(BindingFlags.Public | BindingFlags.Instance));

        ParameterInfo[] parameters = Assert.Single(session.GetConstructors()).GetParameters();
        Assert.Equal(
            [typeof(ScenarioContext), typeof(MindBase), typeof(IGameClock)],
            parameters.Select(static parameter => parameter.ParameterType));
    }

    /// <summary>
    /// WatchRegistry forwards generic retained-observation changes and snapshots only; condition-specific evidence
    /// selection and transitions remain entirely within their concrete watch runtime.
    /// </summary>
    [Fact]
    public void WatchRegistry_DoesNotInterpretConditionSpecificEvidence()
    {
        AssertSourcesContainNoTokens(
            "WatchRegistry must not dispatch concrete condition evidence or outcomes.",
            [RequireSourceFile("game", "src", "Mind", "AI", "Watch", "WatchRegistry.cs")],
            StringComparison.Ordinal,
            "ObservedRelativePosition",
            "ProximityWatch",
            "RelativePositionEvidence",
            "ObservedProximityTransition");
    }

    /// <summary>
    /// Mind's generic ingestion enforces commit identity without any modality-specific branch (AI-001 TR-49):
    /// speech observations supply their <c>(VoiceId, SpeechGroupID, SegmentIndex)</c> identity through the
    /// generic commit-identity contract, and speech correlation belongs to the session coordinator rather than
    /// Mind. Matching rule: ordinal substring <c>ObservedSpeech</c> anywhere in <c>Mind.cs</c> — a concrete
    /// observation-type mention in generic ingestion is the forbidden modality branch.
    /// </summary>
    [Fact]
    public void GenericMindIngestion_HasNoSpeechModalityBranch()
    {
        AssertSourcesContainNoTokens(
            "Mind's generic ingestion must contain no concrete speech observation branch (AI-001 TR-49).",
            [RequireSourceFile("game", "src", "Mind", "Mind.cs")],
            StringComparison.Ordinal,
            "ObservedSpeech");
    }

    /// <summary>
    /// SpeechTool discovers the admission capability through <c>IAdmissionCapableVoice</c> and must never depend
    /// on or cast to a concrete voice class (AI-002 TR-63). Matching rule: case-insensitive substring
    /// <c>aivoice</c> anywhere in <c>SpeechTool.cs</c> — the capability interface name deliberately shares no
    /// characters with the token, so any match is a concrete-voice dependency.
    /// </summary>
    [Fact]
    public void SpeechTool_DoesNotDependOnConcreteVoiceClass()
    {
        AssertSourcesContainNoTokens(
            "SpeechTool must resolve voice admission through the capability interface, never a concrete voice class (AI-002 TR-63).",
            [RequireSourceFile("game", "src", "Mind", "AI", "Tool", "SpeechTool.cs")],
            StringComparison.OrdinalIgnoreCase,
            "aivoice");
    }

    /// <summary>
    /// AgenticMind orchestrates session delivery without interpreting concrete observation records (AI-001
    /// TR-50): no casting or aliasing concrete record types and no feature-payload reads — for speech,
    /// <c>VoiceId</c>, <c>SpeechGroupID</c>, and <c>SegmentIndex</c> inspection belongs to the session-scoped
    /// <c>SpeechTurnContinuationCoordinator</c>. Matching rule: ordinal substring for the concrete observation
    /// record names and TR-50's feature-payload member names anywhere in <c>AgenticMind.cs</c>; the abstract
    /// <c>Observation</c> alias stays allowed.
    /// </summary>
    [Fact]
    public void AgenticMind_DoesNotInterpretConcreteObservationRecords()
    {
        AssertSourcesContainNoTokens(
            "AgenticMind must delegate concrete observation interpretation to the session coordinator (AI-001 TR-50).",
            [RequireSourceFile("game", "src", "Mind", "AI", "AgenticMind.cs")],
            StringComparison.Ordinal,
            "ObservedSpeech",
            "ObservedVisualDescription",
            "ObservedVisualPresence",
            "ObservedRelativePosition",
            "VoiceId",
            "SpeechGroupID",
            "SegmentIndex");
    }

    private static void AssertSourcesContainNoTokens(
        string rule,
        IEnumerable<string> sourceFiles,
        StringComparison comparison,
        params string[] forbiddenTokens)
    {
        AssertSourcesContainNoMatches(
            rule,
            sourceFiles,
            forbiddenTokens.Select(
                token => new SourceMatch(
                    $"'{token}' ({comparison} substring)",
                    line => line.Contains(token, comparison))));
    }

    private static void AssertSourcesContainNoPatterns(
        string rule,
        IEnumerable<string> sourceFiles,
        IReadOnlyList<Regex> forbiddenPatterns)
    {
        AssertSourcesContainNoMatches(
            rule,
            sourceFiles,
            forbiddenPatterns.Select(
                pattern => new SourceMatch(
                    $"'{pattern}' ({pattern.Options} regex)",
                    pattern.IsMatch)));
    }

    private static void AssertSourcesContainNoMatches(
        string rule,
        IEnumerable<string> sourceFiles,
        IEnumerable<SourceMatch> matchers)
    {
        List<string> violations = [];
        foreach (string sourceFile in sourceFiles)
        {
            string[] lines = File.ReadAllLines(sourceFile);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (SourceMatch match in matchers)
                {
                    if (match.IsMatch(lines[index]))
                    {
                        violations.Add(
                            $"{Path.GetRelativePath(RepositoryPath.Get(), sourceFile)}:{index + 1} "
                            + $"matches {match.Description}: '{lines[index].Trim()}'");
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"Module boundary violation — {rule} Offenders:\n{string.Join("\n", violations)}");
        }
    }

    private sealed record SourceMatch(string Description, Func<string, bool> IsMatch);

    private static string RequireSourceFile(params string[] pathSegments)
    {
        string path = RepositoryPath.Get(pathSegments);
        Assert.True(
            File.Exists(path),
            $"Expected production source '{path}' to exist; the boundary guard cannot verify a moved file.");
        return path;
    }

    private static string[] RequireSourceTree(params string[] pathSegments)
    {
        string directory = RepositoryPath.Get(pathSegments);
        string[] files = [.. Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)];
        Assert.NotEmpty(files);
        return files;
    }
}
