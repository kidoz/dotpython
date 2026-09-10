using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DictMappingConstructionTests
{
    [Fact]
    public async Task Dict_SelectsMappingOrPairsBeforeConsumingCustomIterators()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Iterator:
                def __init__(self, values):
                    self.values = values
                    self.index = 0
                def __iter__(self): return self
                def __next__(self):
                    if self.index == len(self.values): raise StopIteration
                    value = self.values[self.index]
                    self.index += 1
                    return value
            class Mapping:
                def keys(self):
                    print('keys')
                    return Iterator(['first', 'second'])
                def __getitem__(self, key):
                    print('get', key)
                    return key + '!'
                def __iter__(self): raise RuntimeError('mapping must use keys')
            print(dict(Mapping(), first='override'))
            class MissingKeys(AttributeError): pass
            class Pairs:
                @property
                def keys(self):
                    print('missing keys')
                    raise MissingKeys('fall back')
                def __iter__(self):
                    print('pairs')
                    return Iterator([Iterator(['value', 7])])
            print(dict(Pairs()))
            class BrokenLookup:
                @property
                def keys(self): raise RuntimeError('lookup failed')
                def __iter__(self): raise AssertionError('must not iterate')
            try: dict(BrokenLookup())
            except RuntimeError as error: print(str(error))
            class BrokenCall:
                def keys(self): raise MissingKeys('call failed')
                def __iter__(self): raise AssertionError('must not iterate')
            try: dict(BrokenCall())
            except MissingKeys as error: print(str(error))
            class AttributeError(ValueError): pass
            class Descendant(AttributeError): pass
            class MisleadingName:
                @property
                def keys(self): raise Descendant('unrelated ancestry')
                def __iter__(self): raise AssertionError('must not iterate')
            try: dict(MisleadingName())
            except ValueError as error: print(str(error))
            """
        );
    }
}
