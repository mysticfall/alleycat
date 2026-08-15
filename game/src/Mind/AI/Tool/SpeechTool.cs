using System.ComponentModel;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Action tool that speaks natural-language output through the owning character's voice.
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
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(speech))
        {
            throw new ArgumentException("Speech request cannot be blank.", nameof(speech));
        }

        string acceptedSpeech = speech.Trim();
        IVoice voice = context.Character.RequireVoice();
        await voice.SpeakAsync(acceptedSpeech, cancellationToken);
        return new AgentToolResult(
            "Spoken through the configured voice.",
            [new ObservedSpeech(ActorId: null, VoiceId: null, Content: acceptedSpeech)]);
    }
}
