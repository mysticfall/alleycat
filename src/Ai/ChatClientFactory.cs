using AlleyCat.Service.Typed;
using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.Ai;

[GlobalClass]
public abstract partial class ChatClientFactory : ResourceFactory<IChatClient>;