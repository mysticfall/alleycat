using System.Collections.ObjectModel;
using AlleyCat.Sense;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Speech;

/// <summary>Hearing-modality-owned speech sense that snapshots accepted voice publications.</summary>
[GlobalClass]
public partial class Hearing : Node, IHearing
{
    private static readonly IReadOnlyList<Type> _perceptTypes =
        new ReadOnlyCollection<Type>([typeof(SpeechPercept)]);

    /// <inheritdoc />
    public event Action<IPercept>? Perceived;

    /// <inheritdoc />
    public IReadOnlyList<Type> PerceptTypes => _perceptTypes;

    /// <inheritdoc />
    public override void _Ready() => AddToGroup(IHearing.GroupName);

    /// <inheritdoc />
    public override void _ExitTree() => RemoveFromGroup(IHearing.GroupName);

    /// <inheritdoc />
    public void ReceiveVoice(string speech, IVoice source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(speech))
        {
            return;
        }

        Perceived?.Invoke(new SpeechPercept(speech, source.Id));
    }
}
