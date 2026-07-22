using System.Reflection;
using DotPython.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotPython.PackageCompatibilityTests;

public sealed class HostingPublicApiTests
{
    [Fact]
    public void NativeHostingSurface_HasExpectedPublicMemberShape()
    {
        Assert.Equal(
            ["InvokeAsync`0/3", "InvokeAsync`1/3", "WarmUpAsync`0/2"],
            GetDeclaredMethodShape(typeof(IDotPythonModuleProvider))
        );
        Assert.Equal(
            ["CreateClient`0/1"],
            GetDeclaredMethodShape(typeof(PythonModuleRegistration<>))
        );
        Assert.Equal(
            ["Definition", "ModuleName", "StatePolicy"],
            GetDeclaredPropertyNames(typeof(PythonModuleRegistration<>))
        );
        Assert.Equal(
            [
                "CreateManaged`0/0",
                "CreateSession`0/0",
                "Create`0/1",
                "DisposeAsync`0/0",
                "GetModule`1/1",
                "GetModule`1/2",
                "WarmUpAsync`1/2",
                "WarmUpAsync`1/3",
            ],
            GetDeclaredMethodShape(typeof(DotPythonHost))
        );
        Assert.Equal(
            [
                "DisposeAsync`0/0",
                "GetModule`1/1",
                "GetModule`1/2",
                "WarmUpAsync`1/2",
                "WarmUpAsync`1/3",
            ],
            GetDeclaredMethodShape(typeof(DotPythonModuleSession))
        );
        Assert.Equal(
            ["MaximumInitializationAttempts", "WarmUpOnHostStart"],
            GetDeclaredPropertyNames(typeof(DotPythonModuleHostingOptions))
        );
        Assert.Equal(
            ["AddDotPythonManaged`0/1", "AddDotPythonModule`1/2", "AddDotPythonModule`1/3"],
            GetDeclaredMethodShape(typeof(DotPythonServiceCollectionExtensions))
        );
    }

    [Fact]
    public void HostingPublicApi_DoesNotExposeManagedBackendTypes()
    {
        var assembly = typeof(DotPythonHost).Assembly;
        var exposedTypes = assembly
            .GetExportedTypes()
            .SelectMany(GetPublicSignatureTypes)
            .SelectMany(GetTypeClosure)
            .Distinct()
            .Where(type =>
                type.Namespace?.StartsWith("DotPython.Runtime.Managed", StringComparison.Ordinal)
                == true
            )
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(exposedTypes);
    }

    private static string[] GetDeclaredMethodShape(Type type) =>
        type.GetMethods(
                BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(method => !method.IsSpecialName)
            .Select(method =>
                $"{method.Name}`{method.GetGenericArguments().Length}/{method.GetParameters().Length}"
            )
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetDeclaredPropertyNames(Type type) =>
        type.GetProperties(
                BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<Type> GetPublicSignatureTypes(Type type)
    {
        yield return type;
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var interfaceType in type.GetInterfaces())
        {
            yield return interfaceType;
        }

        foreach (
            var constructor in type.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            )
        )
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (
            var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            )
        )
        {
            switch (member)
            {
                case MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }

                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case EventInfo eventInfo when eventInfo.EventHandlerType is not null:
                    yield return eventInfo.EventHandlerType;
                    break;
            }
        }
    }

    private static IEnumerable<Type> GetTypeClosure(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nestedType in GetTypeClosure(elementType))
            {
                yield return nestedType;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nestedType in GetTypeClosure(argument))
            {
                yield return nestedType;
            }
        }
    }
}
