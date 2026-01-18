using Godot;
using LanguageExt;
using LanguageExt.Common;
using static LanguageExt.Prelude;
using Error = Godot.Error;

namespace AlleyCat.Common;

public record ParseError(string Message) : Expected(Message, 100);

/// <summary>
/// Represents an error indicating that a required property is not set on a Godot Node.
/// </summary>
public class UnmetDependencyException : InvalidOperationException
{
    /// <summary>
    /// Gets the NodePath where the error occurred.
    /// </summary>
    public NodePath NodePath { get; }

    /// <summary>
    /// Gets the name of the property that is not set.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// Initialises a new instance of the RequiredPropertyNotSetError class.
    /// </summary>
    /// <param name="nodePath">The NodePath where the error occurred.</param>
    /// <param name="propertyName">The name of the property that is not set.</param>
    public UnmetDependencyException(NodePath nodePath, string propertyName)
        : base($"Required property '{propertyName}' is not set on node at path '{nodePath}'.")
    {
        NodePath = nodePath;
        PropertyName = propertyName;
    }

    /// <summary>
    /// Initialises a new instance of the RequiredPropertyNotSetError class with a custom message.
    /// </summary>
    /// <param name="nodePath">The NodePath where the error occurred.</param>
    /// <param name="propertyName">The name of the property that is not set.</param>
    /// <param name="message">A custom error message.</param>
    public UnmetDependencyException(NodePath nodePath, string propertyName, string message)
        : base(message)
    {
        NodePath = nodePath;
        PropertyName = propertyName;
    }

    /// <summary>
    /// Initialises a new instance of the RequiredPropertyNotSetError class with a custom message and inner exception.
    /// </summary>
    /// <param name="nodePath">The NodePath where the error occurred.</param>
    /// <param name="propertyName">The name of the property that is not set.</param>
    /// <param name="message">A custom error message.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public UnmetDependencyException(NodePath nodePath, string propertyName, string message,
        Exception innerException)
        : base(message, innerException)
    {
        NodePath = nodePath;
        PropertyName = propertyName;
    }
}

public static class ErrorExtensions
{
    public static Eff<T> Require<T>(this T? value, string message) =>
        Optional(value).ToEff(LanguageExt.Common.Error.New(message));

    public static T RequireNode<T>(this Node node, T? value, string name)
    {
        return value ?? throw new UnmetDependencyException(node.GetPath(), name);
    }

    extension(Error error)
    {
        public void ThrowOnError() => error.ThrowOnError(None);

        public void ThrowOnError(Func<Error, string> message) => error.ThrowOnError(Some(message));

        private void ThrowOnError(Option<Func<Error, string>> message)
        {
            if (error == Error.Ok) return;

            var code = Enum.GetName(error);

            var arg = message
                .Map(m => m.Invoke(error))
                .IfNone(() => $"Operation failed with code: '{code}(error)'");

            Exception exception = error switch
            {
                Error.Unauthorized or Error.FileNoPermission => new UnauthorizedAccessException(arg),
                Error.ParameterRangeError => new ArgumentOutOfRangeException(null, arg),
                Error.OutOfMemory => new OutOfMemoryException(arg),
                Error.FileBadDrive or Error.FileBadPath or Error.FileNotFound => new FileNotFoundException(arg),
                Error.FileAlreadyInUse or Error.FileCantOpen or Error.FileCantRead or Error.FileCorrupt
                    or Error.FileMissingDependencies or Error.FileUnrecognized => new FileLoadException(arg),
                Error.FileEof => new EndOfStreamException(arg),
                Error.FileCantWrite or Error.CantAcquireResource or Error.CantOpen or Error.CantCreate
                    or Error.AlreadyInUse or Error.Locked => new IOException(arg),
                Error.Timeout => new TimeoutException(arg),
                Error.InvalidData => new InvalidDataException(arg),
                Error.InvalidParameter => new ArgumentException(null, arg),
                _ => new InvalidOperationException(arg)
            };

            throw exception;
        }
    }
}