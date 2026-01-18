using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Env;
using Godot;
using LanguageExt;

namespace AlleyCat.Service;

[GlobalClass]
public abstract partial class NodeFactory : Node, IServiceFactory
{
    public abstract Type ServiceType { get; }

    public abstract Eff<IEnv, object> Service { get; }

    private Subject<Duration>? _processSubject;

    private Subject<Duration>? _physicsProcessSubject;

    protected IObservable<Duration> OnProcess
    {
        get
        {
            _processSubject ??= new Subject<Duration>();

            return _processSubject.AsObservable();
        }
    }

    protected IObservable<Duration> OnPhysicsProcess
    {
        get
        {
            _physicsProcessSubject ??= new Subject<Duration>();

            return _physicsProcessSubject.AsObservable();
        }
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        _processSubject?.OnNext(delta.Seconds());
    }

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        _physicsProcessSubject?.OnNext(delta.Seconds());
    }

    protected override void Dispose(bool disposing)
    {
        _processSubject?.OnCompleted();
        _physicsProcessSubject?.OnCompleted();

        base.Dispose(disposing);
    }
}