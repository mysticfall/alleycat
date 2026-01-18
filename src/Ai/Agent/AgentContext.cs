namespace AlleyCat.Ai.Agent;

public readonly record struct AgentContext(
    IMind Mind,
    IServiceProvider Services
);