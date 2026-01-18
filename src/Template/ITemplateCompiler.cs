using AlleyCat.Env;
using LanguageExt;

namespace AlleyCat.Template;

public interface ITemplateCompiler
{
    Eff<IEnv, ITemplate> Compile(string source);
}