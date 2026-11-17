using System.Reflection;
using AlleyCat.Navigation;
using Microsoft.Extensions.Logging;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private static NavigationRouteSnapshot? RefusalRoute(MotionBricksNavigation nav)
        => (NavigationRouteSnapshot?)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav);

    private sealed class RefusalLogger(Action<string> observe, bool throwOnDiagnostic = false) : ILogger<MotionBricksNavigation>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name != "NavigationRefusalDiagnostic")
            {
                return;
            }
            observe(formatter(state, error));
            if (throwOnDiagnostic)
            {
                throw new InvalidOperationException("Synthetic diagnostic sink failure");
            }
        }
    }
}
