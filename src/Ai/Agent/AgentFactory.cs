using AlleyCat.Service.Typed;
using Godot;
using Microsoft.Agents.AI;

namespace AlleyCat.Ai.Agent;

[GlobalClass]
public abstract partial class AgentFactory : ResourceFactory<AIAgent>;