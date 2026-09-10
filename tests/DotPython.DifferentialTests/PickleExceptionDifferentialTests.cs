using Xunit;

namespace DotPython.DifferentialTests;

public sealed class PickleExceptionDifferentialTests
{
    [Theory]
    [InlineData(
        """
            import pickle
            values = [1]
            error = ValueError(values)
            error.tag = values
            error.loop = error
            payload = pickle.dumps([error, error, values])
            values.append(2)
            first = pickle.loads(payload)
            restored = first[0]
            print(restored is error, first[0] is first[1], type(restored) is ValueError)
            print(restored.args[0], restored.args[0] is restored.tag, restored.tag is first[2])
            print(restored.loop is restored)
            restored.tag.clear()
            second = pickle.loads(payload)
            print(second[0].tag, second[0] is restored)
            """
    )]
    [InlineData(
        """
            import pickle
            import copy
            events = []
            class Custom(ValueError):
                def __init__(self, value):
                    events.append(value)
                    super().__init__(value)
                    self.tag = 'constructor'
            error = Custom('original')
            error.tag = 'stored'
            events.clear()
            payload = pickle.dumps(error)
            print(events)
            restored = pickle.loads(payload)
            print(events, type(restored) is Custom, restored.tag, restored.args)
            events.clear()
            shallow = copy.copy(error)
            deep = copy.deepcopy(error)
            print(events, type(shallow) is Custom, type(deep) is Custom, shallow.tag, deep.tag)
            """
    )]
    [InlineData(
        """
            import pickle
            values = []
            error = ValueError(values)
            values.append(error)
            payload = pickle.dumps(error)
            values.clear()
            restored = pickle.loads(payload)
            print(restored.args[0][0] is restored)
            leaf = ValueError('leaf')
            leaf.tag = [1]
            group = ExceptionGroup('group', [leaf, leaf])
            group.loop = group
            payload = pickle.dumps(group)
            leaf.tag.append(2)
            restored = pickle.loads(payload)
            print(type(restored).__name__, restored.message, len(restored.exceptions))
            print(restored.exceptions[0] is restored.exceptions[1], restored.exceptions[0].tag)
            print(restored.args[1][0] is restored.exceptions[0], restored.loop is restored)
            """
    )]
    [InlineData(
        """
            import pickle
            error = ValueError('stored')
            try:
                raise TypeError('cause')
            except TypeError as cause:
                try:
                    raise error from cause
                except ValueError:
                    pass
            restored = pickle.loads(pickle.dumps(error))
            print(restored.args, restored.__cause__, restored.__context__, restored.__suppress_context__)
            fail = False
            class Custom(ValueError):
                def __init__(self, value):
                    if fail:
                        raise TypeError('constructor failed')
                    super().__init__(value)
            error = Custom('ok')
            fail = True
            payload = pickle.dumps(error)
            try:
                pickle.loads(payload)
            except TypeError as error:
                print(str(error))
            fail = False
            print(pickle.loads(payload).args)
            """
    )]
    [InlineData(
        """
            import pickle
            import copy
            class C:
                pass
            for obj in [C(), ValueError('x')]:
                value = [1]
                obj.a = value
                obj.b = value
                source = [obj, vars(obj), value]
                for clone in [lambda x: pickle.loads(pickle.dumps(x)), copy.deepcopy]:
                    restored = clone(source)
                    obj2 = restored[0]
                    print(vars(obj2) is restored[1], obj2.a is obj2.b, obj2.a is restored[2])
                    print(restored[1]['a'] is obj2.a)
            """
    )]
    [InlineData(
        """
            import pickle
            import copy
            class C:
                pass
            obj = C()
            value = [1]
            obj.a = value
            obj.__dict__[42] = value
            for clone in [lambda x: pickle.loads(pickle.dumps(x)), copy.deepcopy]:
                restored = clone(obj)
                print(restored.__dict__[42], restored.__dict__[42] is restored.a)
            error = ValueError('x')
            error.__dict__[42] = value
            payload = pickle.dumps(error)
            print('dumped')
            for clone in [lambda x: pickle.loads(payload), copy.copy, copy.deepcopy]:
                try:
                    clone(error)
                except TypeError as failure:
                    print(str(failure))
            """
    )]
    public Task Exceptions_MatchReference(string source) =>
        CompatibilityOracle.AssertMatchesAsync(source);
}
