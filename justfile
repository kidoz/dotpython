set positional-arguments

default:
    @just --list

# Format C# and project files with the repository-pinned CSharpier version.
format:
    dotnet tool restore
    dotnet csharpier format .

# Regenerate the checked-in executable parser from the pinned PEG grammar.
parser-generate:
    dotnet run --project src/DotPython.ParserGenerator.Tool -- generate src/DotPython.ParserGenerator/Grammar/python314-subset.gram src/DotPython.ParserGenerator/Generated/PythonGrammar.g.cs

# Fail when the checked-in parser does not match deterministic regeneration.
parser-check:
    dotnet run --project src/DotPython.ParserGenerator.Tool -- check src/DotPython.ParserGenerator/Grammar/python314-subset.gram src/DotPython.ParserGenerator/Generated/PythonGrammar.g.cs

# Check formatting and compile with all configured analyzers and warnings as errors.
lint: parser-check
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
