using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Logging;

[GlobalClass]
public abstract partial class LoggerProviderFactory : Resource
{
    public abstract Eff<IEnv, Unit> Configure(ILoggingBuilder builder);
}