using AlleyCat.Context;

namespace AlleyCat.Body.Eyes;

/// <summary>
/// An embodied observer that can supply observer-relative context.
/// </summary>
public interface IVisualObserver : IEyesHolder, IContextual
{
}
