using System.Text;
using System.Text.RegularExpressions;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;

namespace AlleyCat.Common;

public readonly partial record struct ResourcePath
{
    public enum ResourceScheme
    {
        Resource,
        User
    }

    public string Value { get; }

    public string Path =>
        Value.StartsWith(ResourcePrefix) ? Value[ResourcePrefix.Length..] : Value[UserPrefix.Length..];

    public ResourceScheme Scheme => Value.StartsWith(ResourcePrefix) ? ResourceScheme.Resource : ResourceScheme.User;

    public Seq<string> Segments => Path.Split('/').AsIterable().ToSeq();

    public Option<ResourcePath> Parent
    {
        get
        {
            if (Segments.Count <= 1) return default;

            var sb = new StringBuilder();

            sb.Append(Scheme == ResourceScheme.Resource ? ResourcePrefix : UserPrefix);
            sb.Append(string.Join("/", Segments.Init));

            return new ResourcePath(sb.ToString());
        }
    }

    private const string ResourcePrefix = "res://";

    private const string UserPrefix = "user://";

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SegmentRegex();

    private ResourcePath(string value)
    {
        Value = value;
    }

    public static ResourcePath operator +(ResourcePath parent, string child)
    {
        var prefix = parent.Scheme == ResourceScheme.Resource ? ResourcePrefix : UserPrefix;

        return Create($"{prefix}{parent.Path}/{child}").Value();
    }

    public static implicit operator string(ResourcePath path) => path.Value;

    public static Either<ParseError, ResourcePath> Create(string? path) =>
        Optional(path)
            .Filter(p => !string.IsNullOrWhiteSpace(p))
            .ToEither(new ParseError("Resource path cannot be null or empty."))
            .Bind(ValidateScheme)
            .Bind(ValidateFormat)
            .Map(valid => new ResourcePath(valid));

    private static Either<ParseError, string> ValidateScheme(string p)
    {
        if (p.StartsWith(ResourcePrefix) || p.StartsWith(UserPrefix))
        {
            return Right(p);
        }

        return Left(
            new ParseError($"Resource path must start with '{ResourcePrefix}' or '{UserPrefix}'.")
        );
    }

    private static Either<ParseError, string> ValidateFormat(string p)
    {
        if (p.Contains('\\'))
        {
            return Left(
                new ParseError("Resource path must use forward slashes ('/'), not backslashes ('\\').")
            );
        }

        var relative = p.StartsWith(ResourcePrefix) ? p[ResourcePrefix.Length..] : p[UserPrefix.Length..];

        if (string.IsNullOrEmpty(relative))
        {
            return Left(
                new ParseError("Resource path must contain a relative path after the scheme.")
            );
        }

        if (relative.StartsWith('/') || relative.EndsWith('/'))
        {
            return Left(new ParseError("Resource path must not start or end with '/'."));
        }

        var segments = relative.Split('/');

        foreach (var seg in segments)
        {
            if (string.IsNullOrEmpty(seg))
            {
                return Left(
                    new ParseError("Resource path must not contain empty path segments ('//').")
                );
            }

            if (seg is "." or "..")
            {
                return Left(
                    new ParseError("Resource path must not contain '.' or '..' segments.")
                );
            }

            if (!SegmentRegex().IsMatch(seg))
            {
                return Left(
                    new ParseError(
                        "Resource path contains invalid characters. " +
                        "Allowed: A–Z, a–z, 0–9, '_', '-', '.', and '/'."
                    )
                );
            }
        }

        return Right(p);
    }
}