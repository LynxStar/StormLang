# Storm

Storm is a C# source generator for `.storm` grammars. It generates a lexer, parser, syntax nodes, and runtime support types for the consuming project.

`Storm.SourceGenerator.csproj` targets `netstandard2.0`. Build and run its self-contained tests with:

```sh
dotnet build Storm.slnx
dotnet test Storm.slnx
```

A consumer adds the generator as an analyzer project reference and provides its grammar files as `AdditionalFiles`. The generated `SyntaxNode.Origin` property defaults to `object?`. Consumers that need a strongly typed origin may set the MSBuild property `StormNodeOriginType` to a fully qualified C# type name and expose it through `CompilerVisibleProperty`.
