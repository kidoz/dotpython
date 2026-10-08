using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `pathlib` surface that is implemented, checked against CPython. Every case catches
/// what it exercises, so the reference exits 0. Filesystem cases work beneath a unique
/// directory created in the current directory, which is the module search root in `-c`
/// mode, and print only names relative to it, so no host path reaches the comparison.
/// </summary>
public sealed class PathlibCompatibilityTests
{
    [Fact]
    public Task PurePathSurfaceMatchesTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import os
            import pathlib
            PurePath = pathlib.PurePath
            PurePosixPath = pathlib.PurePosixPath
            Path = pathlib.Path
            PosixPath = pathlib.PosixPath

            def show(label, thunk):
                try:
                    print(label, '=>', repr(thunk()))
                except BaseException as e:
                    print(label, '!!', type(e).__name__ + ':', e)

            show('type name Path a', lambda: type(Path('a')).__name__)
            show('type name Path()', lambda: type(Path()).__name__)
            show('type name PurePath a', lambda: type(PurePath('a')).__name__)
            show('isinstance Path', lambda: (isinstance(Path('a'), Path), isinstance(Path('a'), PosixPath), isinstance(Path('a'), PurePath), isinstance(Path('a'), PurePosixPath)))
            show('isinstance pure', lambda: (isinstance(PurePosixPath('a'), PurePath), isinstance(PurePosixPath('a'), Path), isinstance(PurePosixPath('a'), PurePosixPath)))
            show('bases', lambda: (PurePosixPath.__bases__, Path.__bases__, PosixPath.__bases__))
            show('mro', lambda: PosixPath.__mro__)
            show('repr Path a', lambda: repr(Path('a')))
            show('repr PurePath()', lambda: repr(PurePath()))
            show('str Path()', lambda: str(Path()))
            show('kwarg', lambda: Path('a', foo=1))
            show('bad arg', lambda: Path(3))
            show('bytes arg', lambda: PurePosixPath(b'a/b'))
            show('truediv str', lambda: Path('a') / 'b')
            show('truediv int', lambda: Path('a') / 3)
            show('rtruediv int', lambda: 3 / Path('a'))
            show('lt int', lambda: Path('a') < 1)
            show('eq str', lambda: (Path('a') == 'a', Path('a') != 'a'))
            show('eq classes', lambda: Path('a') == PurePosixPath('a'))
            show('hash eq', lambda: hash(Path('a')) == hash(PurePosixPath('a')))
            show('bool', lambda: bool(Path('a')))
            show('len', lambda: len(Path('a')))
            show('contains', lambda: 'a' in Path('a') and True)
            show('iter', lambda: list(Path('a')))
            show('sorted', lambda: sorted([Path('b/c'), Path('a'), Path('/z'), Path('a.c')]))
            show('fspath', lambda: os.fspath(Path('a/b')))
            show('format', lambda: format(Path('a')))
            show('format spec', lambda: format(Path('a'), '>10'))
            show('fstring', lambda: f'{Path("a")}')
            show('suffixes', lambda: Path('a.tar.gz').suffixes)
            show('suffix stem', lambda: (Path('a.tar.gz').suffix, Path('a.tar.gz').stem, Path('.bashrc').suffix, Path('.bashrc').stem))
            show('parts', lambda: (Path('//a/b').parts, Path('.').parts, Path('///').parts, Path('/').parts))
            show('anchor drive root', lambda: (Path('//a/b').anchor, Path('//a/b').drive, Path('//a/b').root, Path('a').anchor))
            show('parents', lambda: [str(x) for x in Path('a/b/c').parents])
            show('parents len idx', lambda: (len(Path('a/b').parents), str(Path('a/b').parents[0]), str(Path('a/b').parents[-1])))
            show('parents slice', lambda: [str(x) for x in Path('a/b/c').parents[1:]])
            show('parents index err', lambda: Path('a/b').parents[5])
            show('parents repr', lambda: repr(Path('a/b').parents))
            show('parents iter', lambda: [str(x) for x in Path('a/b').parents])
            show('parent', lambda: (str(Path('a/b').parent), str(Path('.').parent), str(Path('/').parent)))
            show('name', lambda: (Path('a/b').name, Path('/').name))
            show('with_name', lambda: str(Path('a/b').with_name('c')))
            show('with_name kw', lambda: str(Path('a/b').with_name(name='c')))
            show('with_name dotdot', lambda: str(Path('a/b').with_name('..')))
            show('with_name empty', lambda: Path('a/b').with_name(''))
            show('with_name slash', lambda: Path('a/b').with_name('x/y'))
            show('with_name 0', lambda: Path('a/b').with_name(0))
            show('with_name root', lambda: Path('/').with_name('a'))
            show('with_name path', lambda: Path('a/b').with_name(Path('c')))
            show('with_stem', lambda: str(Path('a/b.tar.gz').with_stem('x')))
            show('with_suffix', lambda: str(Path('a/b.tar.gz').with_suffix('.txt')))
            show('with_suffix none', lambda: str(Path('a/b.tar.gz').with_suffix('')))
            show('with_suffix nodot', lambda: Path('a/b').with_suffix('txt'))
            show('with_suffix 0', lambda: Path('a/b').with_suffix(0))
            show('with_suffix path', lambda: Path('a/b').with_suffix(Path('c')))
            show('joinpath', lambda: (str(Path('a').joinpath('b', 'c')), str(Path('a').joinpath()), str(Path('a').joinpath('/b'))))
            show('match', lambda: (Path('a/b').match('b'), Path('a/b/c').match('b'), Path('a/b').match('a/*'), Path('/a/b').match('/*')))
            show('match err', lambda: Path('a').match(''))
            show('match cs', lambda: Path('a/b').match('A/*', case_sensitive=False))
            show('full_match', lambda: (Path('a/b').full_match('a/**'), Path('/a').full_match('/*'), Path('a').full_match('*'), Path('a/b').full_match('*')))
            show('relative_to', lambda: (str(Path('a/b/c').relative_to('a')), str(Path('a/b').relative_to('a/b')), str(Path('a/b').relative_to('a', walk_up=True))))
            show('relative_to err', lambda: Path('a/b').relative_to('x'))
            show('relative_to anchors', lambda: Path('a').relative_to('/a', walk_up=True))
            show('is_relative_to', lambda: (Path('a/b').is_relative_to('a'), Path('a/b').is_relative_to(''), Path('a/b').is_relative_to('x')))
            show('is_absolute', lambda: (Path('/a').is_absolute(), Path('//a').is_absolute(), Path('a').is_absolute(), Path('').is_absolute()))
            show('as_posix', lambda: (Path('a//b').as_posix(), Path('/a/b').as_posix()))
            show('as_uri', lambda: (Path('/a/b').as_uri(), Path('/a b').as_uri(), Path('/a%').as_uri(), Path('/a+b').as_uri()))
            show('as_uri rel', lambda: Path('a').as_uri())
            show('as_uri pure rel', lambda: PurePosixPath('a').as_uri())
            show('from_uri', lambda: tuple(str(x) for x in (Path.from_uri('file:///a/b'), Path.from_uri('file:/a/b'), Path.from_uri('FILE:///a/b'), Path.from_uri('file://localhost/a'), Path.from_uri('file:///a%20b'), Path.from_uri('file:///a?q=1'))))
            show('from_uri err', lambda: Path.from_uri('file://host/a'))
            show('from_uri rel', lambda: Path.from_uri('file:a/b'))
            show('from_uri int', lambda: Path.from_uri(3))
            show('from_uri missing', lambda: Path.from_uri())
            show('is_reserved', lambda: PurePath('a').is_reserved())
            show('ordering boundary', lambda: (Path('a') < Path('a.c'), Path('a/b') > Path('a')))
            show('startswith', lambda: (str(Path('a/b')), str(Path('a/./b')), str(Path('a//b'))))
            show('pure build', lambda: str(Path('a') / PurePosixPath('b')))
            show('Path(PurePosixPath)', lambda: type(Path(PurePosixPath('a'))).__name__)
            show('parents of abs', lambda: [str(x) for x in Path('/a/b').parents])
            """
        );

    [Fact]
    public async Task FileSystemReadsBeneathTheSearchRootMatchTheOracle()
    {
        var name = "pathlib-fixture-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Directory.GetCurrentDirectory(), name);
        CreateFixture(directory);
        try
        {
            await CompatibilityOracle.AssertMatchesAsync(
                "base_dir = '"
                    + name
                    + "'\n"
                    + """
                    import os
                    from pathlib import Path

                    base = Path(base_dir)
                    print(base.exists(), base.is_dir(), base.is_file())
                    print((base / 'a.txt').exists(), (base / 'nope.txt').exists())
                    print((base / 'a.txt').is_file(), (base / 'sub').is_dir(), (base / 'sub').is_file())
                    print((base / 'a.txt').is_junction())
                    print(sorted(str(p.relative_to(base)) for p in base.iterdir()))
                    print(sorted(str(p.relative_to(base)) for p in (base / 'sub').iterdir()))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('*')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('*.txt')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('*/*.txt')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('**/*.txt')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('**')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('**/')))
                    print(sorted(str(p.relative_to(base)) for p in base.rglob('*.bin')))
                    print(sorted(str(p.relative_to(base)) for p in base.rglob('c.txt')))
                    print(sorted(str(p.relative_to(base)) for p in base.glob(Path('*.txt'))))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('A.TXT', case_sensitive=False)))
                    print(sorted(str(p.relative_to(base)) for p in base.glob('[ab].txt')))
                    print(repr((base / 'a.txt').read_text()))
                    print(repr((base / 'b.txt').read_text()))
                    print(repr((base / 'sub' / 'c.txt').read_text(encoding='utf-8')))
                    print((base / 'a.txt').read_bytes())
                    print((base / 'sub' / 'deeper' / 'd.bin').read_bytes())
                    print(repr((base / 'a.txt').open().read()))
                    print(repr(open(base / 'a.txt').read()))
                    print(repr(open(os.fspath(base / 'a.txt')).read()))
                    print(os.fspath(base / 'sub' / 'c.txt'))
                    print((base / 'a.txt').as_posix() == os.fspath(base / 'a.txt'))
                    print([(str(p.relative_to(base)), sorted(d), sorted(f)) for p, d, f in base.walk()])
                    print([(str(p.relative_to(base)), sorted(d), sorted(f)) for p, d, f in base.walk(top_down=False)])
                    """
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task FspathProtocolMatchesTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import os
            import pathlib
            Path = pathlib.Path


            def show(label, thunk):
                try:
                    print(label, '=>', repr(thunk()))
                except BaseException as e:
                    print(label, '!!', type(e).__name__ + ':', e)


            class Good:
                def __fspath__(self):
                    return 'good/path'


            class Bytes:
                def __fspath__(self):
                    return b'bytes/path'


            class Bad:
                def __fspath__(self):
                    return 3


            class NoneFspath:
                __fspath__ = None


            show('fspath good', lambda: os.fspath(Good()))
            show('fspath bytes', lambda: os.fspath(Bytes()))
            show('fspath bad', lambda: os.fspath(Bad()))
            show('fspath 3', lambda: os.fspath(3))
            show('fspath none', lambda: os.fspath(None))
            show('fspath str', lambda: os.fspath('a'))
            show('fspath bytes lit', lambda: os.fspath(b'a'))
            show('fspath path', lambda: os.fspath(Path('a')))
            show('fspath 0 args', lambda: os.fspath())
            show('fspath 2 args', lambda: os.fspath('a', 'b'))
            show('fspath none attr', lambda: os.fspath(NoneFspath()))
            show('path good', lambda: str(Path(Good())))
            show('path bytes fspath', lambda: str(Path(Bytes())))
            show('joinpath good', lambda: str(Path('x').joinpath(Good())))
            show('open good', lambda: Path(Good()))
            """
        );

    [Fact]
    public Task KeywordAndClassMethodSurfaceMatchesTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import pathlib
            Path = pathlib.Path
            PurePath = pathlib.PurePath
            PurePosixPath = pathlib.PurePosixPath
            PosixPath = pathlib.PosixPath


            def show(label, thunk):
                try:
                    print(label, '=>', repr(thunk()))
                except BaseException as e:
                    print(label, '!!', type(e).__name__ + ':', e)


            show('glob arity', lambda: Path('.').glob())
            show('glob extra pos', lambda: Path('.').glob('*', True))
            show('glob bad kw', lambda: Path('.').glob('*', nope=1))
            show('exists kw', lambda: Path('.').exists(nope=1))
            show('exists pos', lambda: Path('.').exists(1))
            show('is_dir arg', lambda: Path('.').is_dir(1))
            show('with_name 2', lambda: Path('a/b').with_name('c', 'd'))
            show('with_name 0', lambda: Path('a/b').with_name())
            show('joinpath 1', lambda: Path('a').joinpath('b'))
            show('relative_to 0', lambda: Path('a').relative_to())
            show('relative_to 2 pos', lambda: Path('a').relative_to('a', 'b'))
            show('relative_to kw', lambda: Path('a').relative_to('a', walk_up=1))
            show('match 0', lambda: Path('a').match())
            show('match 2', lambda: Path('a').match('a', 'b'))
            show('full_match kw', lambda: Path('a').full_match('a', case_sensitive=False))
            show('iterdir kw', lambda: list(Path('.').iterdir(nope=1)))
            show('cwd arg', lambda: Path.cwd(1))
            show('home arg', lambda: Path.home(1))
            show('from_uri kw', lambda: Path.from_uri(uri='file:///a'))
            show('from_uri 2', lambda: Path.from_uri('file:///a', 'b'))
            show('parents kw', lambda: Path('a/b').parents(nope=1))
            show('PurePath kw', lambda: Path('a', foo=1))
            show('PosixPath 2', lambda: pathlib.PurePosixPath('a', 'b'))
            show('PurePath empty default', lambda: pathlib.PurePath())
            show('Path none', lambda: Path(None))
            show('Path bool', lambda: Path(True))
            show('Path float', lambda: Path(1.5))
            show('Path list', lambda: Path(['a']))
            show('is_relative_to 0', lambda: Path('a').is_relative_to())
            show('expanduser kw', lambda: Path('~').expanduser(nope=1))
            show('resolve kw', lambda: Path('a').resolve(strict='').name)
            show('absolute kw', lambda: Path('a').absolute(nope=1))
            show('type sorted', lambda: sorted([Path('b'), Path('a')]))
            show('class attr joinpath', lambda: str(PurePath.joinpath(PurePosixPath('a'), 'b')))
            show('class attr exists', lambda: PurePosixPath.exists(PurePosixPath('.')))
            show('class attr with_name', lambda: str(PurePath.with_name(PurePosixPath('a/b'), 'c')))
            show('PosixPath call', lambda: PosixPath('a'))
            show('is_reserved kw', lambda: Path('a').is_reserved(1))
            show('as_posix kw', lambda: Path('a').as_posix(1))
            show('as_uri kw', lambda: Path('/a').as_uri(1))
            show('write_text arity', lambda: Path('a').write_text())
            show('iterdir 1', lambda: list(Path('.').iterdir('x')))
            """
        );

    private static void CreateFixture(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "sub", "deeper"));
        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello\n");
        File.WriteAllText(Path.Combine(directory, "b.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(directory, ".hidden"), "zero");
        File.WriteAllText(Path.Combine(directory, "sub", "c.txt"), "deep");
        File.WriteAllText(Path.Combine(directory, "sub", "deeper", "d.bin"), "deeper");
    }
}
