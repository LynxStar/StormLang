# Storm

Storm is a C# source generator for `.storm` grammars. It generates a lexer, parser, syntax nodes, and runtime support types for the consuming project.

`Storm.SourceGenerator.csproj` targets `netstandard2.0`. Build and run its self-contained tests with:

```sh
dotnet build Storm.slnx
dotnet test Storm.slnx
```

A consumer adds the generator as an analyzer project reference and provides its grammar files as `AdditionalFiles`. The generated `SyntaxNode.Origin` property defaults to `object?`. Consumers that need a strongly typed origin may set the MSBuild property `StormNodeOriginType` to a fully qualified C# type name and expose it through `CompilerVisibleProperty`.

Generated AST captures follow the grammar's child order across labelled references, unlabelled references, and anonymous groups. `Children()` yields canonical owners once; forwarding properties remain readable aliases. Optional captures yield no child when absent. Parent linking and consumer origin stamping traverse this same child tree.

Unnamed alternatives share the union of their captures. Matching public property names and types remain shared; conflicting types receive numeric name suffixes. Anonymous groups with matching field shapes share their `GroupN` property and struct. A different shape at the same group index receives an alternative suffix, such as `Group0Alternative2` and `RNodeGroup0Alternative2`. The parser records the selected alternative internally so its capture order and ownership determine `Children()`; a manually constructed shared node defaults to the first alternative's plan.

Inline arms retain the target rule's captures. Structural prefix matching ignores labels, and the parsed values fill the target's own labels. A direct `head:Prefix` reference retains a `Head` prefix node owning its descendants. Both project the shared parsed prefix without parsing it again. This does not extend which prefix structures validation accepts. Forwarding-only manually constructed aliases and nested inline-block emission remain outside this capture guarantee.
