# MakefileLossless C# port

This directory contains the first C# implementation of `makefile-lossless`, on the `port-to-csharp` branch. The Rust implementation remains unchanged at the repository root.

## Current scope

The .NET 8 library provides a lossless parser foundation: input is grouped into physical/logical lines, common makefile constructs receive broad categories, and concatenating `Makefile.Items` (or calling `ToString()`) reproduces the source exactly, including CRLF, comments, whitespace, continuations, and a missing final newline. It also provides generic parsing and explicit make-variant selection.

This is an incremental port, not feature parity with the Rust crate yet. The rich mutable CST, detailed diagnostics, variant-specific grammar, make-variable/reference parsing, edit APIs, and comprehensive Rust test-suite port remain to be implemented. Classification is intentionally best-effort and must not be used as a validating grammar.

## Build

```sh
dotnet build src/MakefileLossless/MakefileLossless.csproj
```
