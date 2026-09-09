using Xunit;

namespace DotPython.DifferentialTests;

public sealed class PickleAndTemplateDifferentialTests
{
    [Theory]
    [InlineData(
        """
            import pickle
            class Box:
                pass
            values = [1]
            box = Box()
            box.values = values
            source = [box, values, values]
            payload = pickle.dumps(source)
            values.append(2)
            box.values = None
            first = pickle.loads(payload)
            print(first[0].values, first[1], first[0].values is first[1], first[1] is first[2])
            first[1].append(3)
            second = pickle.loads(payload)
            print(second[1], second is first, second[1] is second[2])
            """
    )]
    [InlineData(
        """
            import pickle
            import copy
            values = []
            source = (values,)
            values.append(source)
            values.append(values)
            copied = copy.deepcopy(source)
            print(copied is copied[0][0], copied[0] is copied[0][1])
            payload = pickle.dumps(source)
            values.clear()
            first = pickle.loads(payload)
            second = pickle.loads(payload)
            print(first is first[0][0], first[0] is first[0][1])
            print(second is second[0][0], first is second, first[0] is second[0])
            """
    )]
    [InlineData(
        """
            from string.templatelib import Template, Interpolation
            import string.templatelib
            item = Interpolation(42, 'answer', conversion='r', format_spec='04')
            template = Template('a', 'b', item, Interpolation('end'), 'c', 'd')
            print(template)
            print(template.strings, template.values, list(template))
            print(Template(), Template('a', 'b'))
            print(template.interpolations[0] is item)
            print(type(template) is Template, type(item) is Interpolation)
            print(isinstance(t'x', Template), isinstance(t'{42}'.interpolations[0], Interpolation))
            print(Template, Interpolation, Template.__module__, Interpolation.__module__)
            print(Interpolation(1) == Interpolation(1), item == item)
            print(string.templatelib.Template is Template)
            print(template + t'!')
            """
    )]
    [InlineData(
        """
            from string.templatelib import Template, Interpolation, convert
            class Item:
                def __str__(self): return 'str-é'
                def __repr__(self): return 'repr-é'
            item = Item()
            print(convert(item, None) is item)
            print(convert(item, 's'), convert(item, 'r'), convert(item, conversion='a'))
            print(Interpolation(value=1, format_spec='x'))
            for build in [lambda: Template(1), lambda: Template(x='x'), lambda: Interpolation(), lambda: Interpolation(1, 2), lambda: Interpolation(1, 'x', 'z'), lambda: Interpolation(1, 'x', 2), lambda: Interpolation(1, 'x', None, 2), lambda: convert(1, 'z'), lambda: convert(1, 2)]:
                try:
                    build()
                except Exception as error:
                    print(type(error).__name__, str(error))
            template = Template('a')
            try:
                template.strings = ('b',)
            except AttributeError:
                print('immutable')
            interpolation = Interpolation(1)
            try:
                interpolation.value = 2
            except AttributeError:
                print('immutable')
            """
    )]
    [InlineData(
        """
            from string.templatelib import Template, Interpolation
            item = Interpolation(42, 'answer', 'r', '04')
            print(Interpolation.__match_args__)
            match item:
                case Interpolation(value, expression, conversion, format_spec):
                    print(value, expression, conversion, format_spec)
            match item:
                case Interpolation(value=42, conversion='r'):
                    print('keyword match')
            match Template('a'):
                case Interpolation():
                    print('wrong')
                case Template(strings=('a',)):
                    print('template match')
            try:
                match item:
                    case Interpolation(a, b, c, d, e):
                        pass
            except TypeError:
                print('too many')
            try:
                match item:
                    case Interpolation(a, value=b):
                        pass
            except TypeError:
                print('duplicate')
            try:
                match Template():
                    case Template(value):
                        pass
            except TypeError:
                print('no positional template')
            """
    )]
    [InlineData(
        """
            import pickle
            import copy
            from string.templatelib import Template, Interpolation
            values = []
            item = Interpolation(values)
            template = Template('a', item)
            values.append(template)
            values.append(item)
            payload = pickle.dumps(template)
            print(copy.copy(template) is template, copy.copy(item) is item)
            values.clear()
            first = pickle.loads(payload)
            second = pickle.loads(payload)
            restored_values = first.values[0]
            print(restored_values[0] is first, restored_values[1] is first.interpolations[0])
            print(first is second, first.values[0] is second.values[0])
            """
    )]
    public Task StandardLibrary_MatchesReference(string source) =>
        CompatibilityOracle.AssertMatchesAsync(source);
}
