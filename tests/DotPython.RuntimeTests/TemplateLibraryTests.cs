using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TemplateLibraryTests
{
    [Fact]
    public void TemplateConstructor_CoalescesStringsAndPreservesInterpolationIdentity()
    {
        var interpolation = Assert.IsType<PythonInterpolationValue>(
            ManagedObjectProtocols.Call(
                PythonStandardModules.InterpolationType,
                [PythonWholeNumberValue.Create(42)]
            )
        );
        var template = Assert.IsType<PythonTemplateValue>(
            ManagedObjectProtocols.Call(
                PythonStandardModules.TemplateType,
                [new PythonTextValue("a"), new PythonTextValue("b"), interpolation, interpolation]
            )
        );

        Assert.Equal(["ab", "", ""], template.Strings);
        Assert.Equal(string.Empty, interpolation.Expression);
        Assert.Null(interpolation.Conversion);
        Assert.Same(interpolation, template.Interpolations[0]);
        Assert.Same(interpolation, template.Interpolations[1]);
        Assert.True(PythonBuiltinTypes.IsInstance(template, PythonStandardModules.TemplateType));
        Assert.True(
            PythonBuiltinTypes.IsInstance(interpolation, PythonStandardModules.InterpolationType)
        );
        Assert.False(
            PythonBuiltinTypes.IsInstance(template, PythonStandardModules.InterpolationType)
        );
    }

    [Fact]
    public void PublicTemplateTypes_AgreeWithLiteralTypesAndSupportPatterns()
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            """
            from string.templatelib import Template, Interpolation
            value = 42
            template = t'{value!r:04}'
            item = template.interpolations[0]
            print(type(template) is Template, type(item) is Interpolation)
            print(item == item, item == Interpolation(42, 'value', 'r', '04'))
            match item:
                case Interpolation(value, expression, conversion, format_spec):
                    print(value, expression, conversion, format_spec)
            match template:
                case Template(values=(42,)):
                    print('matched')
            """,
            "template_library.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            $"True True{Environment.NewLine}True False{Environment.NewLine}42 value r 04{Environment.NewLine}matched{Environment.NewLine}",
            output.ToString()
        );
    }
}
