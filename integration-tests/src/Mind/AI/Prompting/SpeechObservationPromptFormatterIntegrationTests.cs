using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage for provisional speech-observation prompt authoring.
/// </summary>
[Headless]
public sealed class SpeechObservationPromptFormatterIntegrationTests
{
    /// <summary>
    /// Known and unknown voices use recognition-aware wording without presenting raw unknown provenance.
    /// </summary>
    [Fact]
    public void Render_UsesRecognisedCharacterOrUnknownVoiceWording()
    {
        SpeechObservationPromptFormatter formatter = new();
        HandlebarsTemplateCompiler compiler = new();

        string known = formatter.Render(new SpeechObservation("raw-known", "Known.Character", "Hello"), compiler);
        string unknown = formatter.Render(new SpeechObservation("secret-raw-voice", null, "Hello"), compiler);

        Assert.Equal("Speech from Known.Character: Hello", known);
        Assert.Equal("Speech from an unknown voice: Hello", unknown);
        Assert.DoesNotContain("secret-raw-voice", unknown, StringComparison.Ordinal);
    }

    /// <summary>
    /// One authored source is compiled lazily once and reused for every observation render.
    /// </summary>
    [Fact]
    public void Render_WhenCalledRepeatedly_CompilesSourceOnce()
    {
        CountingTemplateCompiler compiler = new();
        SpeechObservationPromptFormatter formatter = new();

        _ = formatter.Render(new SpeechObservation("one", "one", "First"), compiler);
        _ = formatter.Render(new SpeechObservation("two", "two", "Second"), compiler);

        Assert.Equal(1, compiler.CompileCount);
    }

    /// <summary>
    /// Observation dispatch remains polymorphic and delegates speech presentation to the configured renderer.
    /// </summary>
    [Fact]
    public void ToPromptString_DelegatesThroughObservationContract()
    {
        CapturingRenderer renderer = new();
        Observation observation = new SpeechObservation("raw", "recognised", "Content");

        string output = observation.ToPromptString(renderer, new HandlebarsTemplateCompiler());

        Assert.Equal("captured", output);
        Assert.Equal("raw", renderer.Observation!.VoiceId);
    }

    private sealed class CountingTemplateCompiler : ITemplateCompiler
    {
        private readonly HandlebarsTemplateCompiler _inner = new();

        public int CompileCount
        {
            get; private set;
        }

        public ITemplate Compile(string source)
        {
            CompileCount++;
            return _inner.Compile(source);
        }
    }

    private sealed class CapturingRenderer : IObservationPromptRenderer
    {
        public SpeechObservation? Observation
        {
            get; private set;
        }

        public string Render(SpeechObservation observation, ITemplateCompiler templateCompiler)
        {
            Observation = observation;
            return "captured";
        }
    }
}
