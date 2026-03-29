using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;

namespace AlleyCat.TestFramework;

internal static class TestCaseUidFactory
{
    public static TestNodeUid Create(MethodInfo method)
    {
        string canonicalMethodIdentity = BuildCanonicalMethodIdentity(method);
        return Create(canonicalMethodIdentity);
    }

    public static TestNodeUid Create(string fullyQualifiedMethodName)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fullyQualifiedMethodName));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static string GetFullyQualifiedMethodName(MethodInfo method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}";

    private static string BuildCanonicalMethodIdentity(MethodInfo method)
    {
        Type declaringType = method.DeclaringType
            ?? throw new ArgumentException("Method must have a declaring type.", nameof(method));

        string assemblyName = declaringType.Assembly.GetName().Name ?? string.Empty;
        string declaringTypeName = declaringType.FullName ?? declaringType.Name;
        int genericArity = method.IsGenericMethodDefinition || method.IsGenericMethod
            ? method.GetGenericArguments().Length
            : 0;
        string parameterTypeList = string.Join(
            ",",
            method.GetParameters().Select(parameter => GetTypeIdentity(parameter.ParameterType)));

        return $"{assemblyName}:{declaringTypeName}.{method.Name}`{genericArity}({parameterTypeList})";
    }

    private static string GetTypeIdentity(Type type)
    {
        if (type.IsByRef)
        {
            return $"{GetTypeIdentity(type.GetElementType()!)}&";
        }

        if (type.IsPointer)
        {
            return $"{GetTypeIdentity(type.GetElementType()!)}*";
        }

        if (type.IsArray)
        {
            return $"{GetTypeIdentity(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (type.IsGenericParameter)
        {
            string genericParameterMarker = type.DeclaringMethod is null ? "!" : "!!";
            return $"{genericParameterMarker}{type.GenericParameterPosition}";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        Type genericDefinition = type.GetGenericTypeDefinition();
        string genericDefinitionName = genericDefinition.FullName ?? genericDefinition.Name;
        string genericArguments = string.Join(",", type.GetGenericArguments().Select(GetTypeIdentity));
        return $"{genericDefinitionName}[{genericArguments}]";
    }
}
