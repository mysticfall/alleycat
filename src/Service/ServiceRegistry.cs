using AlleyCat.Async;
using AlleyCat.Env;
using AlleyCat.Env.Godot;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using LanguageExt.Effects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Service;

[GlobalClass]
public partial class ServiceRegistry : DeferredQueueNode
{
    [Export] public LoggerProviderFactory[] Loggers { get; set; } = [];

    [Export] public ResourceFactory[] Services { get; set; } = [];

    public override void _Ready()
    {
        //FIXME: Resolve dependencies of global services using DI.
        var register =
            from services in SuccessEff(new ServiceCollection())
            from minEnv in liftEff(() =>
            {
                var initEnv = new GodotEnv(GetTree(), this);

                var configuration = new ConfigurationBuilder()
                    .AddJsonFile(
                        initEnv.FileProvider,
                        "res://alleycat.json",
                        optional: false,
                        reloadOnChange: false
                    )
                    .Build();

                services
                    .AddSingleton<IConfiguration>(configuration)
                    .AddLogging(builder =>
                    {
                        builder.SetMinimumLevel(LogLevel.Trace);
                        builder.AddConfiguration(configuration.GetSection("Logging"));

                        foreach (var provider in Loggers)
                        {
                            GD.Print(
                                "Registering a logger provider: ",
                                provider.GetType().FullName
                            );

                            provider
                                .Configure(builder)
                                .Run(initEnv)
                                .IfFail(e => GD.PushError(
                                    "Failed to register a logger provider: ", e));
                        }
                    });

                var provider = services.BuildServiceProvider();

                var env = new GodotEnv(GetTree(), this, provider);

                GodotEnv.Instance = env;

                return env;
            })
            from logger in liftEff(() =>
            {
                var loggerFactory = minEnv.GetRequiredService<ILoggerFactory>();

                return loggerFactory.GetLogger<ServiceRegistry>();
            })
            from _1 in localEff<MinRT, IEnv, Unit>(
                _ => minEnv,
                Services
                    .AsIterable()
                    .Traverse(x =>
                        from service in x.Service
                        from _ in liftEff(() =>
                        {
                            if (logger.IsEnabled(LogLevel.Information))
                            {
                                logger.LogInformation(
                                    "Registering a global service: {service}.",
                                    service.GetType().FullName
                                );
                            }

                            services.AddSingleton(service);
                        })
                        select unit
                    )
                    .IgnoreF()
                    .As()
            )
            from _2 in liftEff(() =>
            {
                var provider = services.BuildServiceProvider();

                GodotEnv.Instance = new GodotEnv(GetTree(), this, provider);

                logger.LogInformation("Services registered successfully");
            })
            select unit;

        register
            .As()
            .Run()
            .IfFail(e =>
            {
                GD.PushError("Error registering services: ", e.ToException());

                GetTree().Quit(-1);
            });
    }
}