using System.Text.Json.Serialization;
using AlleyCat.Env;
using LanguageExt;
using LanguageExt.Common;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Action;

public interface IAction
{
    bool Supports(IActionRequest request);

    Eff<IEnv, ActionResult> Perform(IActionRequest request, IActor actor);
}

public interface IAction<in TReq> : IAction where TReq : IActionRequest
{
    bool IAction.Supports(IActionRequest request) => request is TReq;

    Eff<IEnv, ActionResult> IAction.Perform(IActionRequest request, IActor actor) =>
        request is TReq req
            ? Perform(req, actor)
            : FailEff<ActionResult>(
                Error.New($"Unsupported request type: expected - {typeof(TReq).FullName}, " +
                          $"actual - {request.GetType().FullName}"));

    Eff<IEnv, ActionResult> Perform(TReq request, IActor actor);
}

public interface IActionRequest;

[JsonPolymorphic]
[JsonDerivedType(typeof(Success), "success")]
[JsonDerivedType(typeof(Interrupted), "interrupted")]
[JsonDerivedType(typeof(Invalid), "invalid")]
[JsonDerivedType(typeof(Failure), "failure")]
public abstract record ActionResult
{
    private ActionResult()
    {
    }

    /// <summary>
    /// Indicates that the action was completed successfully.
    /// </summary>
    public sealed record Success(string Message = "Action completed successfully.") : ActionResult;

    /// <summary>
    /// Indicates that the action was started but could not be completed due to external interference
    /// (e.g. being stunned, an enemy blocking, or the target moving out of range).
    /// </summary>
    public sealed record Interrupted(string Reason) : ActionResult;

    /// <summary>
    /// Indicates that the action was not performed because it was unnecessary or the preconditions
    /// were already met (e.g. trying to sit while already sitting).
    /// </summary>
    public sealed record Invalid(string Reason) : ActionResult;

    /// <summary>
    /// Indicates that the action has failed due to an error.
    /// </summary>
    public sealed record Failure(string Reason) : ActionResult;
}