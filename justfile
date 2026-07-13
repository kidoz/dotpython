set positional-arguments

default:
    @just --list

# Format C# and project files with the repository-pinned CSharpier version.
format:
    dotnet tool restore
    dotnet csharpier format .

# Check formatting and compile with all configured analyzers and warnings as errors.
lint:
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
