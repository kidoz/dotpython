set positional-arguments

default:
    @just --list

# Format the native Stable-ABI sources with clang-format.
native-format:
    native/dotpython-abi3/format.sh

# Check native formatting and run clang-tidy with warnings as errors.
native-lint:
    native/dotpython-abi3/format.sh --check
    native/dotpython-abi3/tidy.sh

# Format C#, project files, and native sources with the configured tools.
format: native-format
    dotnet tool restore
    dotnet csharpier format .

# Pinned PEG grammar for the current Python language target (see ADR-015 for the re-pin procedure).
python_grammar := "src/DotPython.ParserGenerator/Grammar/python314-subset.gram"
generated_parser := "src/DotPython.ParserGenerator/Generated/PythonGrammar.g.cs"

# Regenerate the checked-in executable parser from the pinned PEG grammar.
parser-generate:
    dotnet run --project src/DotPython.ParserGenerator.Tool -- generate {{python_grammar}} {{generated_parser}}

# Fail when the checked-in parser does not match deterministic regeneration.
parser-check:
    dotnet run --project src/DotPython.ParserGenerator.Tool -- check {{python_grammar}} {{generated_parser}}

# Check formatting and compile with all configured analyzers and warnings as errors.
lint: parser-check native-lint
    dotnet tool restore
    dotnet csharpier check .
    dotnet build DotPython.sln --configuration Release

# Run the DotPython CLI. Additional arguments are passed through unchanged.
run *args:
    #!/bin/sh
    if [ "${1-}" = "--" ]; then
        shift
    fi
    dotnet run --project src/DotPython.Cli/DotPython.Cli.csproj -- "$@"
