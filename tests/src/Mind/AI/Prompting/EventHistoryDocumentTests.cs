using AlleyCat.Mind.AI.Prompting;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Prompting;

/// <summary>
/// Unit coverage for the authored standalone event-history file convention (AI-003 TR-12/13): verbatim fragment
/// parsing and clear, section-naming authoring errors.
/// </summary>
public sealed class EventHistoryDocumentTests
{
    private const string FragmentDelimiter = "<!-- event-history: speech.observed -->";

    private const string FallbackDelimiter = "<!-- event-history: fallback -->";

    // Authored sources migrated byte-for-byte from the retired npc_event_history.tres resource.
    private const string MigratedFragmentSource =
        "{% if ActorId != blank %}{% if ActorId == character.FullId %}I said: {{ Content }}"
        + "{% else %}Heard {{ ActorId }} say: {{ Content }}{% endif %}"
        + "{% else %}Heard an unknown speaker say: {{ Content }}{% endif %}"
        + "{% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}\n";

    private const string MigratedFallbackSource =
        "((Received {{ TypeKey }} event.)){% if ObservedAt != blank %}"
        + " (at {{ nf(ObservedAt, 1) }}s game time){% endif %}\n";

    /// <summary>The committed standalone file parses into the exact pre-migration template sources.</summary>
    [Fact]
    public void Parse_CommittedAuthoredFile_ProducesVerbatimMigratedSources()
    {
        var document = EventHistoryDocument.Parse(ReadCommittedEventHistoryFile());

        EventHistoryFragment fragment = Assert.Single(document.Fragments);
        Assert.Equal("speech.observed", fragment.TypeKey);
        Assert.Equal(MigratedFragmentSource, fragment.Source);
        Assert.Equal(MigratedFallbackSource, document.FallbackSource);
    }

    /// <summary>Section content excludes the delimiter lines and keeps one trailing newline per section.</summary>
    [Fact]
    public void Parse_SectionsExcludeDelimitersAndKeepTrailingNewline()
    {
        var document = EventHistoryDocument.Parse(
            FragmentDelimiter + "\n"
            + "first line\n"
            + "second line\n"
            + FallbackDelimiter + "\n"
            + "((fallback))\n");

        EventHistoryFragment fragment = Assert.Single(document.Fragments);
        Assert.Equal("speech.observed", fragment.TypeKey);
        Assert.Equal("first line\nsecond line\n", fragment.Source);
        Assert.Equal("((fallback))\n", document.FallbackSource);
    }

    /// <summary>A fallback-only file is valid and yields no fragments.</summary>
    [Fact]
    public void Parse_FallbackOnlyFile_IsValid()
    {
        var document = EventHistoryDocument.Parse(FallbackDelimiter + "\n((only))\n");

        Assert.Empty(document.Fragments);
        Assert.Equal("((only))\n", document.FallbackSource);
    }

    /// <summary>Windows line endings parse to identical sources as Unix line endings.</summary>
    [Fact]
    public void Parse_WindowsLineEndings_ProduceIdenticalSources()
    {
        var unix = EventHistoryDocument.Parse(
            FragmentDelimiter + "\nsource text\n" + FallbackDelimiter + "\n((fallback))\n");
        var windows = EventHistoryDocument.Parse(
            FragmentDelimiter + "\r\nsource text\r\n" + FallbackDelimiter + "\r\n((fallback))\r\n");

        EventHistoryFragment unixFragment = Assert.Single(unix.Fragments);
        EventHistoryFragment windowsFragment = Assert.Single(windows.Fragments);
        Assert.Equal(unixFragment, windowsFragment);
        Assert.Equal(unix.FallbackSource, windows.FallbackSource);
    }

    /// <summary>A blank TypeKey fails clearly (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_BlankTypeKey_ThrowsClearError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("<!-- event-history:   -->\nunused\n" + FallbackDelimiter + "\nf\n"));

        Assert.Contains("nonblank TypeKey", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown TypeKey matching no observation type fails clearly, naming the section (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_UnknownTypeKey_ThrowsClearErrorNamingTheSection()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(
                "<!-- event-history: world.changed -->\nunused\n" + FallbackDelimiter + "\n((f))\n"));

        Assert.Contains("unknown TypeKey 'world.changed'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A case-distinct key matches nothing exactly and fails like any other unknown key.</summary>
    [Fact]
    public void Parse_CaseDistinctTypeKey_IsUnknown()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(
                "<!-- event-history: Speech.Observed -->\nunused\n" + FallbackDelimiter + "\n((f))\n"));

        Assert.Contains("unknown TypeKey 'Speech.Observed'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A duplicate exact TypeKey fails clearly (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_DuplicateExactTypeKey_ThrowsClearError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(
                FragmentDelimiter + "\nfirst\n"
                + FragmentDelimiter + "\nsecond\n"
                + FallbackDelimiter + "\n((f))\n"));

        Assert.Contains(
            "duplicate exact TypeKey 'speech.observed'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A second fallback section is a duplicate exact key failure.</summary>
    [Fact]
    public void Parse_SecondFallbackSection_ThrowsDuplicateKeyError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(FallbackDelimiter + "\nfirst\n" + FallbackDelimiter + "\nsecond\n"));

        Assert.Contains("duplicate exact TypeKey 'fallback'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A missing fallback section fails with the nonblank-fallback guidance (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_MissingFallbackSection_ThrowsNonBlankFallbackGuidance()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(FragmentDelimiter + "\nunmatched\n"));

        Assert.Contains("nonblank fallback", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A blank fallback section fails with the nonblank-fallback guidance (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_BlankFallbackSection_ThrowsNonBlankFallbackGuidance()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse(FallbackDelimiter + "\n\t\n"));

        Assert.Contains("nonblank fallback", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Text outside any section fails clearly, naming the offending line (AI-003 TR-13).</summary>
    [Fact]
    public void Parse_TextOutsideAnySection_ThrowsClearErrorNamingTheLine()
    {
        InvalidOperationException leading = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("stray prose\n" + FallbackDelimiter + "\n((f))\n"));
        InvalidOperationException lateLeading = Assert.Throws<InvalidOperationException>(
            () => EventHistoryDocument.Parse("\n\nstray prose\n" + FallbackDelimiter + "\n((f))\n"));

        Assert.Contains("outside any section on line 1", leading.Message, StringComparison.Ordinal);
        Assert.Contains("outside any section on line 3", lateLeading.Message, StringComparison.Ordinal);
    }

    /// <summary>Blank lines outside sections are tolerated; blank lines inside sections are preserved.</summary>
    [Fact]
    public void Parse_BlankLines_OutsideSectionsAreToleratedInsideSectionsPreserved()
    {
        var document = EventHistoryDocument.Parse(
            "\n"
            + FragmentDelimiter + "\n"
            + "keep\n"
            + "\n"
            + FallbackDelimiter + "\n"
            + "((f))\n");

        EventHistoryFragment fragment = Assert.Single(document.Fragments);
        Assert.Equal("keep\n\n", fragment.Source);
        Assert.Equal("((f))\n", document.FallbackSource);
    }

    /// <summary>Parsing rejects a null source explicitly.</summary>
    [Fact]
    public void Parse_NullSource_ThrowsArgumentNull() => Assert.Throws<ArgumentNullException>(() => EventHistoryDocument.Parse(null!));

    private static string ReadCommittedEventHistoryFile()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "game", "prompts", "event_history.md"));

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlleyCat.sln")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException(
                "Could not locate the repository root from the test binary path.");
    }
}
