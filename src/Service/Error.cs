using LanguageExt;
using LanguageExt.Common;

namespace AlleyCat.Service;

public record NotReadyError(string Message) : Expected(Message, 200);

public record AlreadyDisposedError(string Message) : Expected(Message, 202);

public record ServiceCreationError(
    string Message,
    Option<Error> Inner = default
) : Expected(Message, 203, Inner);