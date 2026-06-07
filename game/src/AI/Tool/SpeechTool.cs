using System.ComponentModel;
using AlleyCat.Body.Voice;
using Godot;

namespace AlleyCat.AI.Tool;

/// <summary>
/// Agent Framework tool that speaks natural-language output through the invocation voice context.
/// </summary>
[Tool]
[GlobalClass]
public partial class SpeechTool : AgentTool
{
    /// <summary>
    /// Creates a speech tool with the default Agent Framework metadata.
    /// </summary>
    public SpeechTool()
    {
        ToolName = "speak";
        ToolDescription = "Speak the supplied text aloud through the configured voice.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Speak;

    [Description("Speak natural-language output through the configured voice.")]
    private static Task<string> Speak(
        [Description("Exact words to say aloud.")] string speech,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (services.GetService(typeof(IVoice)) is not IVoice voice)
        {
            return Task.FromResult("Unable to speak because voice context is unavailable.");
        }

        voice.Speak(speech);
        return Task.FromResult("Spoken through the configured voice.");
    }
}
