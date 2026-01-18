using AlleyCat.Service.Typed;
using Godot;

namespace AlleyCat.Speech.Generator;

[GlobalClass]
public abstract partial class SpeechGeneratorFactory : NodeFactory<ISpeechGenerator>;