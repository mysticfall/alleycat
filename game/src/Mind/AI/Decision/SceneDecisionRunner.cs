using System.Diagnostics;
using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>One character's perspective at the time a non-empty decision pass begins.</summary>
internal sealed record SceneDecisionSnapshot(
    string Subject,
    string? Premise,
    IReadOnlyList<SceneDecisionCharacter> Characters,
    IReadOnlyList<string> History,
    string OwnAction,
    string? CurrentCue,
    double DwellSeconds)
{
    internal const int PremiseCharacterBudget = 600;

    public JsonElement ComposeEvidence()
        => JsonSerializer.SerializeToElement(ComposeFields());

    private Dictionary<string, object?> ComposeFields()
    {
        Dictionary<string, object?> fields = new()
        {
            ["owner"] = Subject,
            ["characters"] = Characters.Select(c => c.Description is null
                ? (object)new
                {
                    id = c.FullId
                }
                : new
                {
                    id = c.FullId,
                    description = c.Description
                }),
            ["recent_history"] = new
            {
                perspective = Subject,
                note = $"first-person lines are from `{Subject}`'s perspective, as they experienced them",
                events = History,
            },
            ["own_action"] = OwnAction,
            ["current_gaze"] = new
            {
                cue = CurrentCue,
                dwell_seconds = Math.Round(DwellSeconds, 1)
            },
        };
        if (!string.IsNullOrWhiteSpace(Premise))
        {
            string text = Premise.Trim();
            if (text.Length > PremiseCharacterBudget)
            {
                int cut = text.LastIndexOf(' ', PremiseCharacterBudget);
                text = $"{text[..(cut > 0 ? cut : PremiseCharacterBudget)]}…";
            }

            fields["premise"] = text;
        }

        return fields;
    }
}

internal sealed record SceneDecisionCharacter(string FullId, string? Description = null);

/// <summary>One gate's own answers and the dispatch material for its local diagnostics.</summary>
internal sealed record SceneDecisionReceipt(IReadOnlyDictionary<string, DecisionAnswer> Answers, string? Failure,
    JsonElement? Evidence, IReadOnlyList<DecisionPayloadQuestion> Questions, long CompositionMs, long DecisionMs);

internal sealed record SceneDecisionGroup(Func<string, IReadOnlyList<DecisionQuestion>> QuestionsFor,
    Action<SceneDecisionReceipt> Complete, CancellationToken Lifetime = default);

/// <summary>A subject's frame-driven list of registered gates. Gather and apply on the Godot thread.</summary>
internal sealed class SceneDecisionRunner(IDecisionClient client, Func<SceneDecisionSnapshot> capture,
    IReadOnlyList<Func<SceneDecisionGroup?>> gates)
{
    private bool _running;

    public Task EvaluateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_running)
        {
            return Task.CompletedTask;
        }

        List<SceneDecisionGroup>? due = null;
        for (int i = 0; i < gates.Count; i++)
        {
            if (gates[i]() is { Lifetime.IsCancellationRequested: false } group)
            {
                (due ??= []).Add(group);
            }
        }

        return due is null ? Task.CompletedTask : DispatchAsync(due, cancellationToken);
    }

    private async Task DispatchAsync(List<SceneDecisionGroup> due, CancellationToken cancellationToken)
    {
        _running = true;
        using var flight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<CancellationTokenRegistration> registrations = [];
        try
        {
            void CancelIfAbandoned()
            {
                if (due.All(g => g.Lifetime.IsCancellationRequested))
                {
                    flight.Cancel();
                }
            }

            foreach (SceneDecisionGroup group in due)
            {
                registrations.Add(group.Lifetime.Register(CancelIfAbandoned));
            }

            long start = Stopwatch.GetTimestamp();
            List<IReadOnlyList<DecisionQuestion>> routes = [];
            JsonElement? evidence = null;
            long sent = start;
            DecisionResult? result = null;
            string? failure = null;
            try
            {
                SceneDecisionSnapshot snapshot = capture();
                evidence = snapshot.ComposeEvidence();
                foreach (SceneDecisionGroup group in due)
                {
                    routes.Add(group.QuestionsFor(snapshot.Subject));
                }

                DecisionRequest request = new(evidence.Value, [.. routes.SelectMany(q => q)]);
                sent = Stopwatch.GetTimestamp();
                result = await client.EvaluateAsync(request, flight.Token);
            }
            catch (OperationCanceledException) when (flight.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception is DecisionException error ? $"decision failure {error.Failure}" : "unexpected evaluation failure";
            }

            long elapsed = (long)Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            for (int i = 0; i < due.Count; i++)
            {
                if (due[i].Lifetime.IsCancellationRequested || flight.IsCancellationRequested)
                {
                    continue;
                }

                List<DecisionPayloadQuestion> descriptions = [];
                Dictionary<string, DecisionAnswer> answers = new(StringComparer.Ordinal);
                foreach (DecisionQuestion q in i < routes.Count ? routes[i] : [])
                {
                    descriptions.Add(new(q.Name, q.Instructions, q is ChoiceQuestion choice
                        ? [.. choice.Options.Select(o => new DecisionPayloadQuestionOption(o.Value, o.Criteria))] : null));
                    if (result?.ByName.TryGetValue(q.Name, out DecisionAnswer? answer) == true)
                    {
                        answers.Add(q.Name, answer);
                    }
                }

                due[i].Complete(new(answers, failure, evidence, descriptions,
                    (long)Stopwatch.GetElapsedTime(start, sent).TotalMilliseconds, elapsed));
            }
        }
        finally
        {
            foreach (CancellationTokenRegistration registration in registrations)
            {
                registration.Dispose();
            }

            _running = false;
        }
    }
}
