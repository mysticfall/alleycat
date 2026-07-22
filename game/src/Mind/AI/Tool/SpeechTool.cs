using System.ComponentModel;
using AlleyCat.Body.Voice;
using AlleyCat.Mind.Observation;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Action tool that speaks natural-language output through the invocation voice context.
/// </summary>
[Tool]
[GlobalClass]
public partial class SpeechTool : AgentTool
{
    /// <summary>
    /// Creates a speech tool with the default model-facing metadata.
    /// </summary>
    public SpeechTool()
    {
        ToolName = "speak";
        ToolDescription = "Speak the supplied text aloud through the configured voice.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Speak;

    [Description("Speak natural-language output through the configured voice.")]
    private static async ValueTask<AgentToolResult> Speak(
        [Description("Exact words to say aloud.")] string speech,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(speech))
        {
            throw new ArgumentException("Speech request cannot be blank.", nameof(speech));
        }

        if (services.GetService(typeof(IVoice)) is not IVoice voice)
        {
            throw new InvalidOperationException("Speech voice context is unavailable.");
        }

        string acceptedSpeech = speech.Trim();
        await voice.SpeakAsync(acceptedSpeech, cancellationToken);
        return new AgentToolResult(
            "Spoken through the configured voice.",
            [new ObservedSpeech(ActorId: null, VoiceId: null, Content: acceptedSpeech)]);
    }
}
