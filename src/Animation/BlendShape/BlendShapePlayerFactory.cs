using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Animation.BlendShape;

[GlobalClass]
public partial class BlendShapePlayerFactory : NodeFactory<IBlendShapePlayer>
{
    [Export] public MeshInstance3D[] Meshes { get; set; } = [];

    [Export] public BlendShapeSet? BlendShapes { get; set; }

    protected override Eff<IEnv, IBlendShapePlayer> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from meshes in Optional(Meshes.AsIterable().ToSeq())
            .Filter(x => !x.IsEmpty)
            .ToEff(Error.New("Mesh list cannot be empty."))
        from blenderShapes in BlendShapes
            .Require("Blend shape set is not set.")
        select (IBlendShapePlayer)new BlendShapePlayer(
            Meshes.AsIterable().ToSeq(),
            blenderShapes,
            OnProcess,
            loggerFactory
        );
}