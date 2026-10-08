using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PathlibExecutionTests
{
    [Fact]
    public void PurePathOperationsNeedNoFilesystem()
    {
        var directory = CreateEmptyDirectory();
        try
        {
            var output = RunIn(
                directory,
                """
                from pathlib import Path, PurePath, PurePosixPath, PosixPath

                p = PurePosixPath('a/b/c.tar.gz')
                print(repr(p), str(p), format(p))
                print(p.parts)
                print((p.anchor, p.drive, p.root, p.name, p.stem, p.suffix, p.suffixes))
                print(str(p.parent), [str(parent) for parent in p.parents])
                print(repr(p.with_name('x')), repr(p.with_stem('s')))
                print(repr(p.with_suffix('.txt')), repr(p.with_suffix('')))
                print(repr(p.joinpath('d', 'e')), repr(p / 'd'))
                print(p.as_posix(), p.is_absolute())
                print(p == PurePosixPath('a/b/c.tar.gz'), p != PurePath('a'))
                print(hash(p) == hash(PurePosixPath('a/b/c.tar.gz')))
                print(sorted(str(item) for item in [PurePosixPath('z'), PurePosixPath('a'), PurePosixPath('a/b')]))
                print(p.match('*.gz'), p.match('a/*/c.tar.gz'), p.match('x/*.gz'))
                print(p.full_match('a/b/c.tar.gz'), p.full_match('*.gz'))
                print(p.relative_to('a'), p.is_relative_to('a'), p.is_relative_to('x'))
                print(repr(Path.from_uri('file:///a/b')), PurePosixPath('/a/b').as_uri())
                print(isinstance(Path('x'), PurePath), isinstance(Path('x'), PosixPath))
                print(PosixPath.__mro__)
                print(PosixPath.__bases__)
                print(type(PurePath('a')).__name__, type(PurePath()).__name__)
                print(type(Path('a')).__name__, repr(PurePath()))
                print((Path('a/b').parts, Path('/a/b').anchor, Path('/a/b').root, Path('/a/b').drive))
                print(str(Path('a/b').relative_to('a')), Path('a/b').is_relative_to('a'))
                try:
                    Path('a.txt').exists()
                    print('exists outside: allowed')
                except PermissionError as error:
                    print('exists outside:', type(error).__name__ + ':', error)
                """
            );

            Assert.Equal(
                Lines(
                    "PurePosixPath('a/b/c.tar.gz') a/b/c.tar.gz a/b/c.tar.gz",
                    "('a', 'b', 'c.tar.gz')",
                    "('', '', '', 'c.tar.gz', 'c.tar', '.gz', ['.tar', '.gz'])",
                    "a/b ['a/b', 'a', '.']",
                    "PurePosixPath('a/b/x') PurePosixPath('a/b/s.gz')",
                    "PurePosixPath('a/b/c.tar.txt') PurePosixPath('a/b/c.tar')",
                    "PurePosixPath('a/b/c.tar.gz/d/e') PurePosixPath('a/b/c.tar.gz/d')",
                    "a/b/c.tar.gz False",
                    "True True",
                    "True",
                    "['a', 'a/b', 'z']",
                    "True True False",
                    "True False",
                    "b/c.tar.gz True False",
                    "PosixPath('/a/b') file:///a/b",
                    "True True",
                    "(<class 'pathlib.PosixPath'>, <class 'pathlib.Path'>, "
                        + "<class 'pathlib.PurePosixPath'>, <class 'pathlib.PurePath'>, "
                        + "<class 'object'>)",
                    "(<class 'pathlib.Path'>, <class 'pathlib.PurePosixPath'>)",
                    "PurePosixPath PurePosixPath",
                    "PosixPath PurePosixPath('.')",
                    "(('a', 'b'), '/', '/', '')",
                    "b True",
                    "exists outside: PermissionError: PosixPath.exists() outside the registered "
                        + "module search roots is not permitted in this runtime slice."
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConcretePathsJoinAndDeriveParts()
    {
        var directory = CreateFixtureRoot();
        try
        {
            var output = RunIn(
                directory,
                """
                from pathlib import Path, PurePath, PosixPath

                base = Path(root)
                p = base / 'sub' / 'c.txt'
                print(type(p).__name__, isinstance(p, Path), isinstance(p, PurePath))
                print(isinstance(p, PosixPath), isinstance(base, PosixPath))
                print(p == Path(root, 'sub', 'c.txt'), Path(PosixPath(root)) == base)
                print(str(Path(root)) == root, base.is_absolute(), p.parts[-2:])
                print(str(p.relative_to(root)), str(p.parent.relative_to(root)), p.name)
                print(p.stem, p.suffix, p.suffixes, str(p.parents[0].relative_to(root)))
                print([str(item) for item in p.relative_to(root).parents])
                print(str(p.with_name('x.txt').relative_to(root)))
                print(str(p.with_stem('s').relative_to(root)))
                print(str(p.with_suffix('.bin').relative_to(root)))
                print(str((p.parent / 'x').relative_to(root)))
                print(str(base.joinpath('a', 'b').relative_to(root)))
                print(p.parents[1] == base, base.anchor, p.root, base.parts[0])
                print(p.as_posix() == str(base) + '/sub/c.txt')
                print(str((p / '..' / '..' / 'a.txt').resolve()) == str(base / 'a.txt'))
                print(base.absolute() == base, p.absolute() == p)
                """
            );

            Assert.Equal(
                Lines(
                    "PosixPath True True",
                    "True True",
                    "True True",
                    "True True ('sub', 'c.txt')",
                    "sub/c.txt sub c.txt",
                    "c .txt ['.txt'] sub",
                    "['sub', '.']",
                    "sub/x.txt",
                    "sub/s.txt",
                    "sub/c.bin",
                    "sub/x",
                    "a/b",
                    "True / / /",
                    "True",
                    "True",
                    "True True"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsInsideTheRegisteredRootSucceed()
    {
        var directory = CreateFixtureRoot();
        try
        {
            var output = RunIn(
                directory,
                """
                from pathlib import Path
                import os

                base = Path(root)
                print(base.exists(), base.is_dir(), base.is_file())
                print((base / 'a.txt').exists(), (base / 'nope.txt').exists())
                print((base / 'a.txt').is_file(), (base / 'sub').is_file(), (base / 'sub').is_dir())
                print((base / 'a.txt').is_junction())
                print(sorted(str(item.relative_to(root)) for item in base.iterdir()))
                print(sorted(str(item.relative_to(root)) for item in (base / 'sub').iterdir()))
                print(sorted(str(item.relative_to(root)) for item in base.glob('*')))
                print(sorted(str(item.relative_to(root)) for item in base.glob('*.txt')))
                print(sorted(str(item.relative_to(root)) for item in base.glob('*/*.txt')))
                print(sorted(str(item.relative_to(root)) for item in base.glob('**/*.txt')))
                print(sorted(str(item.relative_to(root)) for item in base.rglob('*.bin')))
                print(sorted(str(item.relative_to(root)) for item in base.glob(Path('*.txt'))))
                print(sorted(str(item.relative_to(root)) for item in base.glob('A.TXT', case_sensitive=False)))
                print(repr((base / 'a.txt').read_text()))
                print(repr((base / 'b.txt').read_text()))
                print(repr((base / 'sub' / 'c.txt').read_text(encoding='utf-8')))
                print(repr((base / 'sub' / 'c.txt').read_text(errors='strict')))
                print((base / 'a.txt').read_bytes())
                print((base / 'sub' / 'deeper' / 'd.bin').read_bytes())
                print(repr((base / 'a.txt').open().read()))
                print(repr(open(base / 'a.txt').read()))
                print(repr(open(os.fspath(base / 'a.txt')).read()))
                print(os.fspath(base / 'sub' / 'c.txt') == str(base / 'sub' / 'c.txt'))
                print([(str(path.relative_to(root)), sorted(dirs), sorted(files)) for path, dirs, files in base.walk()])
                print([(str(path.relative_to(root)), sorted(dirs), sorted(files)) for path, dirs, files in base.walk(top_down=False)])
                try:
                    (base / 'a.txt').iterdir()
                    print('iterdir file: allowed')
                except OSError as error:
                    print('iterdir file:', type(error).__name__)
                try:
                    base.glob('')
                    print('empty pattern: allowed')
                except ValueError as error:
                    print('empty pattern:', type(error).__name__ + ':', error)
                try:
                    base.glob(3)
                    print('int pattern: allowed')
                except TypeError as error:
                    print('int pattern:', type(error).__name__ + ':', error)
                """
            );

            Assert.Equal(
                Lines(
                    "True True False",
                    "True False",
                    "True False True",
                    "False",
                    "['.hidden', 'a.txt', 'b.txt', 'sub']",
                    "['sub/c.txt', 'sub/deeper']",
                    "['.hidden', 'a.txt', 'b.txt', 'sub']",
                    "['a.txt', 'b.txt']",
                    "['sub/c.txt']",
                    "['a.txt', 'b.txt', 'sub/c.txt']",
                    "['sub/deeper/d.bin']",
                    "['a.txt', 'b.txt']",
                    "['a.txt']",
                    "'hello\\n'",
                    "'one\\ntwo\\n'",
                    "'deep'",
                    "'deep'",
                    "b'hello\\n'",
                    "b'deeper'",
                    "'hello\\n'",
                    "'hello\\n'",
                    "'hello\\n'",
                    "True",
                    "[('.', ['sub'], ['.hidden', 'a.txt', 'b.txt']), "
                        + "('sub', ['deeper'], ['c.txt']), ('sub/deeper', [], ['d.bin'])]",
                    "[('sub/deeper', [], ['d.bin']), ('sub', ['deeper'], ['c.txt']), "
                        + "('.', ['sub'], ['.hidden', 'a.txt', 'b.txt'])]",
                    "iterdir file: OSError",
                    "empty pattern: ValueError: Unacceptable pattern: ''",
                    "int pattern: TypeError: argument should be a str or an os.PathLike object "
                        + "where __fspath__ returns a str, not 'int'"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsOutsideTheRegisteredRootRaisePermissionError()
    {
        var directory = CreateFixtureRoot();
        try
        {
            var output = RunIn(
                directory,
                """
                from pathlib import Path

                def show(label, action):
                    try:
                        action()
                        print(label, 'allowed')
                    except PermissionError as error:
                        print(label, type(error).__name__ + ':', error)

                show('exists', lambda: Path('/etc/hosts').exists())
                show('is_file', lambda: Path('/etc/hosts').is_file())
                show('is_dir', lambda: Path('/etc').is_dir())
                show('read_text', lambda: Path('/etc/hosts').read_text())
                show('read_bytes', lambda: Path('/etc/hosts').read_bytes())
                show('iterdir', lambda: list(Path('/etc').iterdir()))
                show('glob', lambda: list(Path('/etc').glob('*')))
                show('walk', lambda: list(Path('/etc').walk()))
                show('resolve', lambda: Path('/etc/hosts').resolve(strict=True))
                show('open', lambda: Path('/etc/hosts').open())
                show('relative', lambda: Path('a.txt').exists())
                show('parent', lambda: Path('..').exists())
                show('escaped', lambda: Path(root, '..', 'escape.txt').read_text())
                print(Path('/etc/hosts').is_absolute(), Path('/etc/hosts').absolute() == Path('/etc/hosts'))
                print(Path('/etc/hosts').resolve() == Path('/etc/hosts'))
                """
            );

            Assert.Equal(
                Lines(
                    "exists " + OutsideRootRefusal("exists"),
                    "is_file " + OutsideRootRefusal("is_file"),
                    "is_dir " + OutsideRootRefusal("is_dir"),
                    "read_text " + OutsideRootRefusal("read_text"),
                    "read_bytes " + OutsideRootRefusal("read_bytes"),
                    "iterdir " + OutsideRootRefusal("iterdir"),
                    "glob " + OutsideRootRefusal("glob"),
                    "walk " + OutsideRootRefusal("walk"),
                    "resolve PermissionError: PosixPath.resolve() with strict=True outside the "
                        + "registered module search roots is not permitted in this runtime slice.",
                    "open PermissionError: open() outside the registered module search roots is "
                        + "not permitted in this runtime slice.",
                    "relative " + OutsideRootRefusal("exists"),
                    "parent " + OutsideRootRefusal("exists"),
                    "escaped " + OutsideRootRefusal("read_text"),
                    "True True",
                    "True"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WritesMetadataAndTextOptionsAreRefused()
    {
        var directory = CreateFixtureRoot();
        try
        {
            var output = RunIn(
                directory,
                """
                from pathlib import Path

                base = Path(root)
                target = base / 'a.txt'

                def show(label, action):
                    try:
                        action()
                        print(label, 'allowed')
                    except (PermissionError, NotImplementedError, TypeError) as error:
                        print(label, type(error).__name__ + ':', error)

                show('write_text', lambda: target.write_text('x'))
                show('write_bytes', lambda: target.write_bytes(b'x'))
                show('mkdir', lambda: (base / 'new').mkdir())
                show('mkdir parents', lambda: (base / 'new').mkdir(parents=True, exist_ok=True))
                show('touch', lambda: (base / 'new').touch())
                show('unlink', lambda: target.unlink())
                show('unlink missing_ok', lambda: target.unlink(missing_ok=True))
                show('rmdir', lambda: (base / 'sub').rmdir())
                show('rename', lambda: target.rename(base / 'z.txt'))
                show('replace', lambda: target.replace(base / 'z.txt'))
                show('chmod', lambda: target.chmod(0o644))
                show('symlink_to', lambda: (base / 'l').symlink_to(target))
                show('hardlink_to', lambda: (base / 'l').hardlink_to(target))
                show('copy', lambda: target.copy(base / 'z.txt'))
                show('stat', lambda: target.stat())
                show('lstat', lambda: target.lstat())
                show('owner', lambda: target.owner())
                show('group', lambda: target.group())
                show('readlink', lambda: target.readlink())
                show('is_symlink', lambda: target.is_symlink())
                show('is_mount', lambda: target.is_mount())
                show('samefile', lambda: target.samefile(target))
                show('read_text latin-1', lambda: target.read_text(encoding='latin-1'))
                show('read_text replace', lambda: target.read_text(errors='replace'))
                show('read_text newline', lambda: target.read_text(newline='\n'))
                show('open latin-1', lambda: target.open(encoding='latin-1'))
                show('walk follow_symlinks', lambda: list(base.walk(follow_symlinks=True)))
                show('walk on_error', lambda: list(base.walk(on_error=print)))
                show('glob recurse_symlinks', lambda: list(base.glob('*', recurse_symlinks=True)))
                show('exists follow_symlinks', lambda: target.exists(follow_symlinks=False))
                show('write_text data', lambda: target.write_text(3))
                show('write_bytes data', lambda: target.write_bytes('x'))
                """
            );

            Assert.Equal(
                Lines(
                    "write_text " + WriteRefusal("write_text"),
                    "write_bytes " + WriteRefusal("write_bytes"),
                    "mkdir " + WriteRefusal("mkdir"),
                    "mkdir parents " + WriteRefusal("mkdir"),
                    "touch " + WriteRefusal("touch"),
                    "unlink " + WriteRefusal("unlink"),
                    "unlink missing_ok " + WriteRefusal("unlink"),
                    "rmdir " + WriteRefusal("rmdir"),
                    "rename " + WriteRefusal("rename"),
                    "replace " + WriteRefusal("replace"),
                    "chmod " + WriteRefusal("chmod"),
                    "symlink_to " + WriteRefusal("symlink_to"),
                    "hardlink_to " + WriteRefusal("hardlink_to"),
                    "copy " + WriteRefusal("copy"),
                    "stat " + MetadataRefusal("stat"),
                    "lstat " + MetadataRefusal("lstat"),
                    "owner " + MetadataRefusal("owner"),
                    "group " + MetadataRefusal("group"),
                    "readlink " + MetadataRefusal("readlink"),
                    "is_symlink " + MetadataRefusal("is_symlink"),
                    "is_mount " + MetadataRefusal("is_mount"),
                    "samefile " + MetadataRefusal("samefile"),
                    "read_text latin-1 " + TextOptionRefusal("read_text", "encoding"),
                    "read_text replace " + TextOptionRefusal("read_text", "errors"),
                    "read_text newline " + TextOptionRefusal("read_text", "newline"),
                    "open latin-1 " + TextOptionRefusal("Path.open", "encoding"),
                    "walk follow_symlinks NotImplementedError: PosixPath.walk() does not support "
                        + "follow_symlinks=True in this runtime slice (symlinks are out of scope).",
                    "walk on_error NotImplementedError: PosixPath.walk() does not support on_error "
                        + "in this runtime slice.",
                    "glob recurse_symlinks NotImplementedError: PosixPath.glob() does not support "
                        + "recurse_symlinks=True in this runtime slice (symlinks are out of scope).",
                    "exists follow_symlinks NotImplementedError: exists() does not support "
                        + "follow_symlinks=False in this runtime slice (symlinks are out of scope).",
                    "write_text data TypeError: data must be str, not int",
                    "write_bytes data TypeError: memoryview: a bytes-like object is required, "
                        + "not 'str'"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadCapIsEnforcedForOpenAndReadBytes()
    {
        var directory = CreateEmptyDirectory();
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "large.bin"),
                new byte[(8 * 1024 * 1024) + 1]
            );

            var readBytes = ExecuteIn(
                directory,
                "from pathlib import Path\n(Path(root) / 'large.bin').read_bytes()\n"
            );
            Assert.False(readBytes.Success);
            Assert.Contains(
                "read_bytes() beyond the 8388608 byte limit is not supported in this runtime slice.",
                readBytes.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray()
            );

            var openLarge = ExecuteIn(
                directory,
                "from pathlib import Path\nopen(Path(root) / 'large.bin')\n"
            );
            Assert.False(openLarge.Success);
            Assert.Contains(
                "open() beyond the 8388608 byte limit is not supported in this runtime slice.",
                openLarge.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WriteModesThroughOpenFollowTheSliceDiagnostic()
    {
        var directory = CreateFixtureRoot();
        try
        {
            var written = ExecuteIn(
                directory,
                "from pathlib import Path\nPath(root, 'a.txt').open('w')\n"
            );
            Assert.False(written.Success);
            Assert.Contains(
                "open() mode 'w' is not supported in this runtime slice.",
                written.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray()
            );

            var binary = ExecuteIn(
                directory,
                "from pathlib import Path\nPath(root, 'a.txt').open('rb')\n"
            );
            Assert.False(binary.Success);
            Assert.Contains(
                "open() mode 'rb' is not supported in this runtime slice.",
                binary.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FspathProtocolAndKeywordArguments()
    {
        var directory = CreateEmptyDirectory();
        try
        {
            var output = RunIn(
                directory,
                """
                import os
                from pathlib import Path, PurePosixPath

                class Good:
                    def __fspath__(self):
                        return 'good/path'

                class BytesFspath:
                    def __fspath__(self):
                        return b'bytes/path'

                class Bad:
                    def __fspath__(self):
                        return 3

                def show(label, action):
                    try:
                        print(label, '=>', repr(action()))
                    except BaseException as error:
                        print(label, '!!', type(error).__name__ + ':', error)

                show('good', lambda: os.fspath(Good()))
                show('bytes', lambda: os.fspath(BytesFspath()))
                show('bad', lambda: os.fspath(Bad()))
                show('int', lambda: os.fspath(3))
                show('none', lambda: os.fspath(None))
                show('str', lambda: os.fspath('a'))
                show('bytes literal', lambda: os.fspath(b'a'))
                show('path', lambda: os.fspath(PurePosixPath('a')))
                show('missing', lambda: os.fspath())
                show('too many', lambda: os.fspath('a', 'b'))
                show('path good', lambda: PurePosixPath(Good()))
                show('path bad', lambda: PurePosixPath(BytesFspath()))
                show('joinpath good', lambda: PurePosixPath('x').joinpath(Good()))
                show('path int', lambda: PurePosixPath(3))
                show('path none', lambda: PurePosixPath(None))
                show('bad kw', lambda: PurePosixPath('a', foo=1))
                print(repr(Path.from_uri('file:///a/b')), repr(Path.from_uri(uri='file:///a%20b')))
                show('from_uri missing', lambda: Path.from_uri())
                show('from_uri many', lambda: Path.from_uri('file:///a', 'b'))
                show('from_uri conflict', lambda: Path.from_uri('file:///a', uri='file:///b'))
                show('from_uri authority', lambda: Path.from_uri('file://example.com/a'))
                print(PurePosixPath(root) == Path(root))
                """
            );

            Assert.Equal(
                Lines(
                    "good => 'good/path'",
                    "bytes => b'bytes/path'",
                    "bad !! TypeError: expected Bad.__fspath__() to return str or bytes, not int",
                    "int !! TypeError: expected str, bytes or os.PathLike object, not int",
                    "none !! TypeError: expected str, bytes or os.PathLike object, not NoneType",
                    "str => 'a'",
                    "bytes literal => b'a'",
                    "path => 'a'",
                    "missing !! TypeError: fspath() missing required argument 'path' (pos 1)",
                    "too many !! TypeError: fspath() takes at most 1 argument (2 given)",
                    "path good => PurePosixPath('good/path')",
                    "path bad !! TypeError: argument should be a str or an os.PathLike object "
                        + "where __fspath__ returns a str, not 'bytes'",
                    "joinpath good => PurePosixPath('x/good/path')",
                    "path int !! TypeError: argument should be a str or an os.PathLike object "
                        + "where __fspath__ returns a str, not 'int'",
                    "path none !! TypeError: argument should be a str or an os.PathLike object "
                        + "where __fspath__ returns a str, not 'NoneType'",
                    "bad kw !! TypeError: PurePath.__init__() got an unexpected keyword argument 'foo'",
                    "PosixPath('/a/b') PosixPath('/a b')",
                    "from_uri missing !! TypeError: Path.from_uri() missing 1 required positional "
                        + "argument: 'uri'",
                    "from_uri many !! TypeError: Path.from_uri() takes 2 positional arguments but "
                        + "3 were given",
                    "from_uri conflict !! TypeError: Path.from_uri() got multiple values for "
                        + "argument 'uri'",
                    "from_uri authority !! ValueError: file:// scheme is supported only on "
                        + "localhost",
                    "True"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string OutsideRootRefusal(string name) =>
        "PermissionError: PosixPath."
        + name
        + "() outside the registered module search roots is not permitted in this runtime "
        + "slice.";

    private static string WriteRefusal(string name) =>
        "PermissionError: Path."
        + name
        + "() requires a filesystem write capability that this runtime slice does not "
        + "provide.";

    private static string MetadataRefusal(string name) =>
        "NotImplementedError: PosixPath."
        + name
        + "() is not supported in this runtime slice (file metadata, ownership and symlinks "
        + "are out of scope).";

    private static string TextOptionRefusal(string owner, string option) =>
        "NotImplementedError: "
        + owner
        + "() does not support the '"
        + option
        + "' argument in this runtime slice (decoding is always UTF-8).";

    private static string RunIn(string directory, string body)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine(
            new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
        ).Execute(
            Source(directory, body),
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static ManagedExecutionResult ExecuteIn(string directory, string body) =>
        new ManagedPythonEngine(
            new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
        ).Execute(
            Source(directory, body),
            "<test>",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static string Source(string directory, string body) =>
        "root = " + Quote(directory) + "\n" + body;

    private static string Quote(string value) =>
        "'"
        + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
        + "'";

    private static string CreateFixtureRoot()
    {
        var directory = CreateEmptyDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "sub", "deeper"));
        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello\n");
        File.WriteAllText(Path.Combine(directory, "b.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(directory, ".hidden"), "zero");
        File.WriteAllText(Path.Combine(directory, "sub", "c.txt"), "deep");
        File.WriteAllText(Path.Combine(directory, "sub", "deeper", "d.bin"), "deeper");
        return directory;
    }

    private static string CreateEmptyDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dotpython-pathlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
