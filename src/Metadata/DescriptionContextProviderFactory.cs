using AlleyCat.Env;
using AlleyCat.Template;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Metadata;

[GlobalClass]
public partial class DescriptionContextProviderFactory : TemplateContextProviderFactory
{
    [Export(PropertyHint.MultilineText)] public string? Description { get; set; }

    protected override Eff<IEnv, ITemplateContextProvider> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from description in Description.Require("Description is not set.")
        from compiler in service<ITemplateCompiler>()
        from template in compiler.Compile(description)
        select (ITemplateContextProvider)new DescriptionContextProvider(template);
}