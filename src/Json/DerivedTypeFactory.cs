using System.Text.Json.Serialization.Metadata;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Json;

[GlobalClass]
public partial class DerivedTypeFactory : ResourceFactory<JsonDerivedType>
{
    [Export] public string? Type { get; set; }

    [Export] public string? Discriminator { get; set; }

    protected override Eff<IEnv, JsonDerivedType> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from type in Type
            .Require("Type is not set.")
            .Bind(x => Optional(System.Type.GetType(x))
                .ToEff(Error.New($"The specified type '{x}' could not be found."))
            )
        from discriminator in Discriminator
            .Require("Discriminator is not set.")
        select new JsonDerivedType(type, discriminator);
}