using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

[GlobalClass]
public partial class MarkerFactory : NodeFactory<IMarker>
{
    [Export] public Marker3D? Marker { get; set; }

    [Export] public TagListFactory? Tags { get; set; }

    protected override Eff<IEnv, IMarker> CreateService(ILoggerFactory loggerFactory) =>
        from id in MarkerId.Create(Name).ToEff(identity)
        from marker in Marker.Require("Marker node is not set.")
        from tagList in Tags
            .Require("Tag list is not set.")
            .Bind(x => x.TypedService)
        let globalTransform = IO.lift(() => marker.GlobalTransform)
        select (IMarker)new Marker(id, tagList.Tags, globalTransform);
}