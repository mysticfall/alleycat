using System.Globalization;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using HandlebarsDotNet;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Error = LanguageExt.Common.Error;
using static LanguageExt.Prelude;

namespace AlleyCat.Template.Handlebars.Helper;

public class NumberFormatHelper(ILoggerFactory? loggerFactory = null) : IHandlebarsHelper
{
    private readonly ILogger _logger = loggerFactory.GetLogger<NumberFormatHelper>();

    public Eff<IEnv, Unit> Register(IHandlebars handlebars) => liftEff(() =>
    {
        _logger.LogDebug("Registering Handlebars helper 'numberFormat'.");

        handlebars.RegisterHelper("nf", (writer, _, parameters) =>
        {
            if (parameters.Length < 1 || parameters[0] is null)
            {
                _logger.LogWarning("Handlebars helper 'nf' called without a parameter.");
                return;
            }

            if (!TryToDecimal(parameters[0], out var number))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Handlebars helper 'nf' called with invalid parameter: {param}.",
                        parameters[0]
                    );
                }

                return;
            }

            var precision = 3;

            if (parameters.Length >= 2 && TryToInt(parameters[1], out var p))
            {
                precision = Math.Clamp(p, 0, 99);
            }

            var format = $"F{precision}";
            var output = number.ToString(format, CultureInfo.CurrentCulture);

            writer.WriteSafeString(output);
        });
    }).MapFail(e =>
        Error.New("Failed to register Handlebars helper 'nf'.", e)
    );

    private static bool TryToDecimal(object? value, out decimal number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case decimal d:
                number = d;
                return true;
            case byte b:
                number = b;
                return true;
            case sbyte sb:
                number = sb;
                return true;
            case short s:
                number = s;
                return true;
            case ushort us:
                number = us;
                return true;
            case int i:
                number = i;
                return true;
            case uint ui:
                number = ui;
                return true;
            case long l:
                number = l;
                return true;
            case ulong ul:
                number = ul;
                return true;
            case float f:
                number = (decimal)f;
                return true;
            case double dbl:
                number = (decimal)dbl;
                return true;
            case string s when !string.IsNullOrWhiteSpace(s):
                return decimal.TryParse(
                    s,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.CurrentCulture,
                    out number
                ) || decimal.TryParse(
                    s,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out number
                );
            default:
                try
                {
                    number = Convert.ToDecimal(value, CultureInfo.CurrentCulture);
                    return true;
                }
                catch
                {
                    try
                    {
                        number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        number = 0;
                        return false;
                    }
                }
        }
    }

    private static bool TryToInt(object? value, out int result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case int i:
                result = i;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case long l and >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case uint ui and <= int.MaxValue:
                result = (int)ui;
                return true;
            case string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                try
                {
                    result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    result = 0;
                    return false;
                }
        }
    }
}

[GlobalClass]
public partial class NumberFormatHelperFactory : HandlebarsHelperFactory
{
    protected override Eff<IEnv, IHandlebarsHelper> CreateService(
        ILoggerFactory loggerFactory
    ) => SuccessEff<IHandlebarsHelper>(new NumberFormatHelper(loggerFactory));
}