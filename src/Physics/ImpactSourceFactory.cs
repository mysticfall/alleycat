using AlleyCat.Audio;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Physics;

[GlobalClass]
public partial class ImpactSourceFactory : NodeFactory<ImpactSource>
{
    [Export] public Area3D? Area { get; set; }

    [Export] public AudioStreamPlayer3D? AudioPlayer { get; set; }

    [Export] public SoundSet? SoundSet { get; set; }

    [Export(PropertyHint.Range, "0,10,0.1")]
    public float SilentPeriod { get; set; } = 0.5f;

    protected override Eff<IEnv, ImpactSource> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from area in Area.Require("Area is not set.")
        from audioPlayer in AudioPlayer.Require("AudioPlayer is not set.")
        from soundSet in SoundSet.Require("SoundSet is not set.")
        select new ImpactSource(
            area,
            soundSet,
            audioPlayer,
            SilentPeriod.Seconds(),
            loggerFactory
        );
}