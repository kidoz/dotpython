using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TextTraversalCompatibilityTests
{
    [Fact]
    public Task LoneSurrogatesSurviveEverySequenceAccess() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xd800, 0xdbff, 0xdc00, 0xdc80, 0xdcff, 0xdfff):
                value = chr(point)
                print(point, len(value), [ord(char) for char in value], ord(value[0]), ord(value[-1]))
                print(value[:].encode('ascii', 'backslashreplace'), value[::-1].encode('ascii', 'backslashreplace'))
            """
        );

    [Fact]
    public Task CompleteLoneSurrogateInventoryRetainsItsCodepoints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in range(0xd800, 0xe000):
                value = chr(point)
                print(point, ord(next(iter(value))), ord(next(reversed(value))), ord(value[0]))
            """
        );

    [Fact]
    public Task MixedStringsUsePythonCharacterIndices() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A😀' + chr(0xd800) + 'é' + chr(0xdcff) + '𐀀Z'
            print(len(source), [ord(char) for char in source])
            for index in range(-len(source), len(source)):
                print(index, ord(source[index]))
            print([ord(char) for char in reversed(source)])
            """
        );

    [Fact]
    public Task PositiveAndNegativeSlicesPreserveSourceCharacters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'a😀' + chr(0xd800) + 'b' + chr(0xdbff) + 'Ω𐀀z'
            for start, stop, step in ((None,None,None),(1,5,1),(None,None,-1),(None,None,2),(None,None,-2),(-7,-1,3),(6,0,-2),(10,20,1),(2,2,1),(None,None,10**100),(None,None,-10**100)):
                value = source[slice(start,stop,step)]
                print(start, stop, step, value.encode('ascii','backslashreplace'))
            print(source[:] is source, source[::1] is source)
            """
        );

    [Fact]
    public Task SliceIndexCallbacksRunInStepStartStopOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, name, value): self.name=name; self.value=value
                def __index__(self): print(self.name); return self.value
            source = 'A😀' + chr(0xd800) + 'Z'
            print(source[Index('start',1):Index('stop',4):Index('step',1)].encode('ascii','backslashreplace'))
            print(ord(source[Index('scalar',2)]))
            """
        );

    [Fact]
    public Task IndexErrorsAndBooleanIndicesMatchPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = chr(0xd800) + '😀'
            for index in (True,False,-3,2,10**100,-10**100,1.5,'a',None,(1,)):
                try: print(source[index].encode('ascii','backslashreplace'))
                except Exception as error: print(type(error).__name__, str(error))
            try: print(source[::0])
            except Exception as error: print(type(error).__name__,str(error))
            """
        );

    [Fact]
    public Task IteratorHintsTrackCharactersAndExhaustion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A😀' + chr(0xd800) + 'Z'
            for cursor in (iter(source), reversed(source)):
                print(cursor.__length_hint__())
                print(ord(next(cursor)),cursor.__length_hint__())
                print([ord(char) for char in cursor],cursor.__length_hint__())
                print(next(cursor,'done'),next(cursor,'done'))
            """
        );

    [Fact]
    public Task IteratorConsumersPreserveSurrogates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = chr(0xdcff) + 'x' + chr(0xd800)
            print([ord(value) for value in list(source)])
            print([ord(value) for value in tuple(source)])
            print([(i,ord(value)) for i,value in enumerate(source)])
            a,b,c=source
            print(ord(a),ord(b),ord(c))
            print('|'.join(source).encode('ascii','backslashreplace'))
            """
        );

    [Fact]
    public Task RepresentationsEscapeLoneSurrogatesAndBothQuoteKinds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (chr(0xd800),chr(0xdcff),'a😀'+chr(0xd800),'\n\t\\'+chr(0xdc80),"'\""+chr(0xdfff),"'"+chr(0xd800),'"'+chr(0xdcff)):
                print(repr(value),ascii(value),repr([value]),repr((value,)))
            """
        );

    [Fact]
    public Task RepresentationConversionsSharePreservedText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = chr(0xd800) + '😀' + chr(0xdcff)
            print(f'{source!r}')
            print(f'{source!a}')
            print('%r' % source)
            print('%a' % source)
            print('{!a}'.format(source))
            print('{!r}'.format(source))
            """
        );

    [Fact]
    public Task BoundedFindCountAndAffixesKeepLoneSurrogateNeedles() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source='a😀'+chr(0xd800)+'b'+chr(0xdcff)+'z'
            for needle in (chr(0xd800),chr(0xdcff),'😀','missing'):
                print(source.find(needle),source.find(needle,1,5),source.count(needle),source.count(needle,1,5))
                print(source.startswith(needle,2,3),source.endswith(needle,2,3))
            print(source.index(chr(0xdcff)),source.count(''))
            """
        );

    [Fact]
    public Task EmptyNeedleReplacementDoesNotSplitSupplementaryCharacters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A😀' + chr(0xd800) + 'Z'
            for count in (-1,0,1,2,3,4,5,20):
                print(count,source.replace('','|',count).encode('ascii','backslashreplace'))
            print(source.replace('','|').encode('ascii','backslashreplace'))
            """
        );

    [Fact]
    public Task CapitalizationPreservesUnpairedCharacters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in (chr(0xd800)+'ABC','a'+chr(0xdcff)+'BC','😀'+chr(0xd800)+'ABC',''):
                print(source.capitalize().encode('ascii','backslashreplace'))
            """
        );

    [Fact]
    public Task ChunkBoundariesPreservePairsAndLoneCharacters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for padding in (254,255,256,257):
                source = 'a'*padding+'😀'+chr(0xd800)+'b'+chr(0xdcff)
                print(len(source),[ord(c) for c in source[-4:]],source[-4:][::-1].encode('ascii','backslashreplace'))
            """
        );

    [Fact]
    public Task FailedIndexConversionLeavesSavedCursorsUsable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source=chr(0xd800)+'😀Z'
            cursor=iter(source)
            class Bad:
                def __index__(self): raise ValueError('index failed')
            for operation in (0,1):
                try:
                    if operation == 0: source[Bad()]
                    else: source[Bad():]
                except ValueError as error: print(str(error))
            print([ord(c) for c in cursor])
            """
        );

    [Fact]
    public Task IndependentCursorsDoNotShareTextOffsets() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source='😀'+chr(0xd800)+'Z'
            a=iter(source); b=iter(source); r=reversed(source)
            print(ord(next(a)),ord(next(a)),ord(next(b)),ord(next(r)))
            print([ord(c) for c in a],[ord(c) for c in b],[ord(c) for c in r])
            """
        );
}
