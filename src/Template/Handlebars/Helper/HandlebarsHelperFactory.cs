using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using HandlebarsDotNet;
using LanguageExt;

namespace AlleyCat.Template.Handlebars.Helper;

public interface IHandlebarsHelper
{
    Eff<IEnv, Unit> Register(IHandlebars handlebars);
}

[GlobalClass]
public abstract partial class HandlebarsHelperFactory : ResourceFactory<IHandlebarsHelper>;