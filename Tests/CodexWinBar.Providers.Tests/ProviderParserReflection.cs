using System.Reflection;

namespace CodexWinBar.Providers.Tests;

internal static class ProviderParserReflection
{
    public static object Invoke(string typeName, string methodName, params object?[] arguments)
    {
        var type = Type.GetType(typeName + ", CodexWinBar.Providers", throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, methodName);
        try
        {
            return method.Invoke(null, arguments)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
