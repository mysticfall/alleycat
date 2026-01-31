using LanguageExt;
using LanguageExt.Common;

namespace AlleyCat.Scene;

public record InvalidSceneError(string Message) : Expected(Message, 301);

public record SceneLoadingError(
    string Message,
    Option<Error> Inner = default
) : Expected(Message, 300, Inner);