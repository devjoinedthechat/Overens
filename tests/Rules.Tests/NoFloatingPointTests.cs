using System.Reflection;

namespace Overens.Rules.Tests;

public sealed class NoFloatingPointTests
{
    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Money held in binary floating point loses øre. Nothing in the rules assembly may use it.</summary>
    [Fact]
    public void Nothing_in_the_rules_assembly_holds_or_passes_a_float_or_a_double()
    {
        var used = typeof(PayEngine).Assembly.GetTypes().SelectMany(type => new[]
        {
            type.GetFields(Everything).Select(field => (Where: $"{type.Name}.{field.Name}", Type: field.FieldType)),
            type.GetProperties(Everything).Select(property => (Where: $"{type.Name}.{property.Name}", Type: property.PropertyType)),
            type.GetMethods(Everything).SelectMany(method => method.GetParameters()
                .Select(parameter => (Where: $"{type.Name}.{method.Name}({parameter.Name})", Type: parameter.ParameterType))
                .Append((Where: $"{type.Name}.{method.Name}", Type: method.ReturnType))),
        }.SelectMany(members => members));

        Assert.Empty(used.Where(member => Holds(member.Type)).Select(member => member.Where));

        static bool Holds(Type type) =>
            type == typeof(double) || type == typeof(float)
            || (type.HasElementType && Holds(type.GetElementType()!))
            || type.GetGenericArguments().Any(Holds);
    }
}
