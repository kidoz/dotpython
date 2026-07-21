set positional-arguments

native_build_dir := "build/native-abi3"

default:
    @just --list

# Format the native Stable-ABI Rust workspace.
native-format:
    cargo fmt --manifest-path native/dotpython-abi3/Cargo.toml --all

# Check native formatting and run Clippy with warnings as errors.
native-lint:
    cargo fmt --manifest-path native/dotpython-abi3/Cargo.toml --all -- --check
    cargo clippy --manifest-path native/dotpython-abi3/Cargo.toml --workspace --all-targets -- -D warnings

# Build, verify, test, and stage the native Stable-ABI artifacts.
native-test:
    cargo test --manifest-path native/dotpython-abi3/Cargo.toml --workspace
    native/dotpython-abi3/build.sh "{{native_build_dir}}"

# Format C#, project files, and native Rust sources with the configured tools.
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
