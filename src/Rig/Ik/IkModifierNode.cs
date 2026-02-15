using System.Reactive.Linq;
using System.Reactive.Subjects;
using Godot;
using LanguageExt;

namespace AlleyCat.Rig.Ik;

[GlobalClass]
public partial class IkModifierNode : SkeletonModifier3D
{
    private readonly Subject<Duration> _subject = new();

    public IObservable<Duration> OnIkProcess => _subject.AsObservable();

    public override void _ProcessModificationWithDelta(double delta)
    {
        base._ProcessModificationWithDelta(delta);

        _subject.OnNext(delta.Seconds());
    }

    protected override void Dispose(bool disposing)
    {
        _subject.OnCompleted();

        base.Dispose(disposing);
    }
}