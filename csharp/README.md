# MakefileLossless C# port

This directory contains an incremental C# port of `makefile-lossless`. It targets .NET 8 and keeps the original Rust crate at the repository root.

## Variant-aware parsing

`Makefile.ParseWithVariant(text, MakefileVariant.GnuMake)` restricts recognized syntax and reports obvious cross-dialect constructs. `BsdMake` recognizes `.if`/`.for`/`.include` directives, `NMake` recognizes `!IF`/`!INCLUDE`, space-indented commands, and caret continuations in macro definitions, and `PosixMake` reports common GNU/BSD/nmake extensions. `Makefile.Parse(text)` accepts the supported dialect forms and detects nmake directives when present.

All parsing is recovery-oriented. `Items` preserve original text exactly, including CRLF, comments, continuations, whitespace and missing final newlines. `Errors` and `IsValid` expose basic diagnostics. The parser is still a first-stage line-oriented port, not feature parity with Rust's detailed CST, grammar, error recovery, variable-reference parser, or editing API.

## Build and test

```sh
dotnet build src/MakefileLossless/MakefileLossless.csproj
dotnet test tests/MakefileLossless.Tests/MakefileLossless.Tests.csproj
```
