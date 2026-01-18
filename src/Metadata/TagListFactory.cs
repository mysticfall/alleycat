using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

[GlobalClass]
public partial class TagListFactory : ResourceFactory<TagList>
{
    [Export] public string[] Tags { get; set; } = [];

    protected override Eff<IEnv, TagList> CreateService(
        ILoggerFactory loggerFactory
    ) => toSet(Tags)
        .Map(x => x.Trim())
        .Traverse(Tag.Create)
        .Map(x => new TagList(x))
        .As()
        .ToEff(identity);
}