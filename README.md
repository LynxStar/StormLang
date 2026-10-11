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

## Tokens and literals

A token expression may name a fragment or another token; either one's pattern is inlined, looked up in the token's own mode before the default mode. A reference that cannot be inlined there, or a cycle of them, is an error.

A string literal in a rule matches the token the lexer produces for its text. That is the earliest default-mode token whose pattern accepts the whole text, because the lexer keeps the longest match and breaks equal-length ties by declaration order. When that token is spelled exactly like the literal it is simply reused. When it is a pattern, such as `token IDENT = [a-z]+;` under `rule Word = "if";`, the parser also requires its lexeme to be the literal's text, so `if` parses as `Word` while `iffy` stays one `IDENT` that `Word` refuses. Left unlabelled, such a literal's node property keeps the name its synthetic token would have, `__SYNTH_if`, apart from any `IDENT` property beside it. Token priorities are unchanged: `IDENT` still matches `if` wherever a rule names `IDENT`. A synthetic token is created only for text no declared token accepts, and a literal whose token is hidden is refused like a rule naming that hidden token.

## Imports

`import "path";` merges the imported file's declarations into the importing file's names. `import "path" as Alias;` makes them reachable from the importing file only as `Alias.Name`. The root and every file it reaches through unnamespaced imports share one set of names, so such a file may use a name another of them declares. A name inside a file reached only through an alias means what it means in that file, its own declarations and its unnamespaced imports, never the root's declaration of the same name, whichever alias reaches the file first; one that means nothing there is reported against that file. A namespaced file's own namespaced imports nest under its alias. A file is resolved once, so two aliases of it, or an alias of a file the root also imports plainly, reach the same declarations rather than copies of them.

Declarations reached only through an alias are generated after the root's, under their qualified name joined with underscores: `L.Foo` becomes `L_FooNode` and `ParseL_Foo`, numbered past any identifier the grammar already uses, so `ParseL_Foo2` when the root itself declares `L_Foo`. Root declarations keep their own names, so adding a namespaced import never renames the root grammar's generated API. Diagnostics name declarations by their qualified names.

## Validation

An alternative that an earlier alternative of the same rule wins on every input it can match is unreachable under ordered choice and is reported as error STORM0006, which stops generation. The proof stops at its conservative limits, so an alternative it cannot settle is not reported. A finding that an inline block takes part in, whether as the shadowed alternative, as an earlier alternative credited with success on its prefix, or among an inline block's arms, remains a warning, as inline-rule-prefix requirement 5 prescribes.
