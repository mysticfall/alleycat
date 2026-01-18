using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;

namespace AlleyCat.Ai.Agent.Tool;

[GlobalClass]
public abstract partial class AgentToolFactory : ResourceFactory<Iterable<IAgentTool>>;