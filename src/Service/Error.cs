using LanguageExt.Common;

namespace AlleyCat.Service;

public record NotReadyError(string Message) : Expected(Message, 200);

public record AlreadyDisposedError(string Message) : Expected(Message, 202);