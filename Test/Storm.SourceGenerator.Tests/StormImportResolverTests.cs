using Storm.SourceGenerator.Diagnostics;
using Storm.SourceGenerator.Resolve;

namespace Storm.SourceGenerator.Tests;

public class StormImportResolverTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Creates a file reader delegate backed by an in-memory dictionary.
    /// Keys should use forward-slash paths matching the canonical form.
    /// </summary>
    private static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
    {
        return path =>
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            if(files.TryGetValue(normalized, out var contents))
            {
                return contents;
            }

            return null;
        };
    }

    // ---------------------------------------------------------------
    //  Cycle handling (A imports B, B imports A)
    // ---------------------------------------------------------------

    [Fact]
    public void Resolve_CyclicImports_DoesNotInfiniteLoop()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"b.storm\";\ntoken Foo = \"foo\";",
            ["root/b.storm"] = "import \"a.storm\";\ntoken Bar = \"bar\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.FilesByPath.Count);
        Assert.Contains("root/a.storm", result.FilesByPath.Keys);
        Assert.Contains("root/b.storm", result.FilesByPath.Keys);
    }

    [Fact]
    public void Resolve_SelfImport_DoesNotInfiniteLoop()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"a.storm\";\ntoken Foo = \"foo\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.True(result.Succeeded);
        _ = Assert.Single(result.FilesByPath);
    }

    [Fact]
    public void Resolve_TriangleCycle_VisitsEachFileOnce()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"b.storm\";\nimport \"c.storm\";\ntoken A = \"a\";",
            ["root/b.storm"] = "import \"c.storm\";\ntoken B = \"b\";",
            ["root/c.storm"] = "import \"a.storm\";\ntoken C = \"c\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.FilesByPath.Count);
        // Verify DFS pre-order: a first, then b (first import of a), then c (import of b)
        Assert.Equal("root/a.storm", result.OrderedFiles[0].Key);
        Assert.Equal("root/b.storm", result.OrderedFiles[1].Key);
        Assert.Equal("root/c.storm", result.OrderedFiles[2].Key);
    }

    // ---------------------------------------------------------------
    //  Unresolved import error reporting
    // ---------------------------------------------------------------

    [Fact]
    public void Resolve_UnresolvedImport_EmitsImportNotFoundDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"missing.storm\";\ntoken Foo = \"foo\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_IMPORT_NOT_FOUND);
    }

    [Fact]
    public void Resolve_UnresolvedImport_DiagnosticPointsToImportingFile()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"nonexistent.storm\";\ntoken Foo = \"foo\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.False(result.Succeeded);
        var diag = result.Diagnostics.First(d => d.Id == StormDiagnosticIds.STORM_IMPORT_NOT_FOUND);
        Assert.Equal("root/a.storm", diag.FilePath);
    }

    // ---------------------------------------------------------------
    //  Basic resolution
    // ---------------------------------------------------------------

    [Fact]
    public void Resolve_SingleFileNoImports_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "token Foo = \"foo\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/main.storm");

        Assert.True(result.Succeeded);
        _ = Assert.Single(result.FilesByPath);
        Assert.Equal("root/main.storm", result.RootPath);
    }

    [Fact]
    public void Resolve_ChainedImports_ResolvesAll()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"b.storm\";\ntoken A = \"a\";",
            ["root/b.storm"] = "import \"c.storm\";\ntoken B = \"b\";",
            ["root/c.storm"] = "token C = \"c\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.FilesByPath.Count);
    }

    [Fact]
    public void Resolve_RelativePaths_ResolvedAgainstImportingFileDirectory()
    {
        var files = new Dictionary<string, string>
        {
            ["project/grammar/main.storm"] = "import \"../shared/tokens.storm\";\nrule Start = Foo;",
            ["project/shared/tokens.storm"] = "token Foo = \"foo\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("project/grammar/main.storm");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.FilesByPath.Count);
        Assert.Contains("project/shared/tokens.storm", result.FilesByPath.Keys);
    }

    [Fact]
    public void Resolve_SameFileImportedTwice_ResolvedOnce()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "import \"shared.storm\";\nimport \"b.storm\";\ntoken A = \"a\";",
            ["root/b.storm"] = "import \"shared.storm\";\ntoken B = \"b\";",
            ["root/shared.storm"] = "token Shared = \"shared\";",
        };

        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var result = resolver.Resolve("root/a.storm");

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.FilesByPath.Count);
        // shared.storm should appear only once in ordered files
        Assert.Equal(1, result.OrderedFiles.Count(kv => kv.Key == "root/shared.storm"));
    }
}

public class StormGrammarTests
{
    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static Func<string, string?> InMemoryFileReader(Dictionary<string, string> files)
    {
        return path =>
        {
            var normalized = StormImportResolver.CanonicalizePath(path);
            if(files.TryGetValue(normalized, out var contents))
            {
                return contents;
            }

            return null;
        };
    }

    private static StormGrammar BuildGrammar(Dictionary<string, string> files, string rootPath)
    {
        var resolver = new StormImportResolver(InMemoryFileReader(files));
        var resolution = resolver.Resolve(rootPath);
        return StormGrammar.Build(resolution);
    }

    // ---------------------------------------------------------------
    //  Duplicate-name detection
    // ---------------------------------------------------------------

    [Fact]
    public void Build_DuplicateNameInUnnamespacedImports_EmitsDuplicateNameDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"a.storm\";\nimport \"b.storm\";\ntoken Main = \"main\";",
            ["root/a.storm"] = "token Dup = \"a\";",
            ["root/b.storm"] = "token Dup = \"b\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.False(grammar.Succeeded);
        Assert.Contains(grammar.Diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_DUPLICATE_NAME &&
            d.Message.Contains("Dup"));
    }

    [Fact]
    public void Build_DuplicateNameBetweenRootAndImport_EmitsDuplicateNameDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"a.storm\";\ntoken Dup = \"main\";",
            ["root/a.storm"] = "token Dup = \"a\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.False(grammar.Succeeded);
        Assert.Contains(grammar.Diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_DUPLICATE_NAME);
    }

    [Fact]
    public void Build_SameNameInNamespacedImport_NoDuplicate()
    {
        // Namespaced imports don't conflict with unnamespaced declarations
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"a.storm\" as NS;\ntoken Foo = \"main\";",
            ["root/a.storm"] = "token Foo = \"a\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        // "Foo" is in unnamespaced, "NS.Foo" is in namespaced
        Assert.NotNull(grammar.LookupByName("Foo"));
        Assert.NotNull(grammar.LookupByName("NS.Foo"));
    }

    // ---------------------------------------------------------------
    //  Namespaced vs unnamespaced merging
    // ---------------------------------------------------------------

    [Fact]
    public void Build_NamespacedImport_DeclarationsAccessibleViaQualifiedName()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"tokens.storm\" as Tok;\nrule Start = Tok.Ident;",
            ["root/tokens.storm"] = "token Ident = [a-z]+;",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.NotNull(grammar.LookupByName("Tok.Ident"));
        Assert.Contains(grammar.NamespacedDeclarations, kv => kv.Key == "Tok.Ident");
    }

    [Fact]
    public void Build_UnnamespacedImport_DeclarationsMergedIntoRoot()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"tokens.storm\";\nrule Start = Ident;",
            ["root/tokens.storm"] = "token Ident = [a-z]+;",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.NotNull(grammar.LookupByName("Ident"));
        // Ident should be in the main declarations list, not in namespaced
        Assert.DoesNotContain(grammar.NamespacedDeclarations, kv => kv.Key == "Ident");
    }

    [Fact]
    public void Build_MixedNamespacedAndUnnamespaced_BothAccessible()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"common.storm\";\nimport \"extra.storm\" as Ex;\ntoken Main = \"main\";",
            ["root/common.storm"] = "token Common = \"common\";",
            ["root/extra.storm"] = "token Extra = \"extra\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.NotNull(grammar.LookupByName("Main"));
        Assert.NotNull(grammar.LookupByName("Common"));
        Assert.NotNull(grammar.LookupByName("Ex.Extra"));
        Assert.Null(grammar.LookupByName("Extra")); // not accessible without qualifier
    }

    // ---------------------------------------------------------------
    //  Declaration order matches the spec (R6.8)
    // ---------------------------------------------------------------

    [Fact]
    public void Build_DeclarationOrder_RootFirstThenUnnamespacedThenNamespaced()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"unns.storm\";\nimport \"ns.storm\" as NS;\ntoken RootToken = \"root\";",
            ["root/unns.storm"] = "token UnnamespacedToken = \"unns\";",
            ["root/ns.storm"] = "token NamespacedToken = \"ns\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);

        var names = grammar.Declarations.Select(d => d.Name).ToList();

        // Unnamespaced import declarations first (imports processed before own declarations)
        Assert.Equal("UnnamespacedToken", names[0]);
        // Then root declarations
        Assert.Equal("RootToken", names[1]);
        // Then namespaced declarations last
        Assert.Equal("NamespacedToken", names[2]);
    }

    [Fact]
    public void Build_DeclarationOrder_MultipleUnnamespacedImports_InImportAppearanceOrder()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"first.storm\";\nimport \"second.storm\";\ntoken Root = \"root\";",
            ["root/first.storm"] = "token First = \"first\";",
            ["root/second.storm"] = "token Second = \"second\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);

        var names = grammar.Declarations.Select(d => d.Name).ToList();

        // Imports processed first in import-appearance order
        Assert.Equal("First", names[0]);
        Assert.Equal("Second", names[1]);
        // Then root declarations
        Assert.Equal("Root", names[2]);
    }

    [Fact]
    public void Build_DeclarationOrder_DFSPreOrder_NestedImports()
    {
        // main imports a, a imports b
        // Imports first: b (imported by a), then a, then main's own
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"a.storm\";\ntoken Root = \"root\";",
            ["root/a.storm"] = "import \"b.storm\";\ntoken A = \"a\";",
            ["root/b.storm"] = "token B = \"b\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);

        var names = grammar.Declarations.Select(d => d.Name).ToList();

        // B first (imported by A, which is imported by main — deepest import first)
        Assert.Equal("B", names[0]);
        // Then A (imported by main, after its own imports)
        Assert.Equal("A", names[1]);
        // Then Root (main's own declarations last)
        Assert.Equal("Root", names[2]);
    }

    // ---------------------------------------------------------------
    //  Lookup
    // ---------------------------------------------------------------

    [Fact]
    public void LookupByName_UnqualifiedName_FindsDeclaration()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\ntoken Foo = \"foo\";\nrule Bar = Foo;",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.NotNull(grammar.LookupByName("Foo"));
        Assert.NotNull(grammar.LookupByName("Bar"));
        Assert.Null(grammar.LookupByName("Baz"));
    }

    [Fact]
    public void LookupByName_QualifiedName_FindsNamespacedDeclaration()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"lib.storm\" as Lib;\nrule Start = Lib.Tok;",
            ["root/lib.storm"] = "token Tok = \"tok\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.NotNull(grammar.LookupByName("Lib.Tok"));
        Assert.Null(grammar.LookupByName("Tok")); // not accessible without qualifier
    }

    // ---------------------------------------------------------------
    //  Edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void Build_EmptyRootFile_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\n",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.Empty(grammar.Declarations);
    }

    [Fact]
    public void Build_CyclicImports_ProducesCorrectGrammar()
    {
        var files = new Dictionary<string, string>
        {
            ["root/a.storm"] = "grammar Test;\nimport \"b.storm\";\ntoken A = \"a\";",
            ["root/b.storm"] = "import \"a.storm\";\ntoken B = \"b\";",
        };

        var grammar = BuildGrammar(files, "root/a.storm");

        Assert.True(grammar.Succeeded);
        var names = grammar.Declarations.Select(d => d.Name).ToList();
        Assert.Equal(2, names.Count);
        // B comes first (imported by a, processed before a's own declarations)
        // a.storm imports b.storm first, so B is processed first
        // Then A (a.storm's own declaration)
        Assert.Equal("B", names[0]);
        Assert.Equal("A", names[1]);
    }

    // ---------------------------------------------------------------
    //  Grammar declaration validation
    // ---------------------------------------------------------------

    [Fact]
    public void Build_NoGrammarDeclaration_EmitsNoRootGrammarDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "token Foo = \"foo\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.False(grammar.Succeeded);
        Assert.Contains(grammar.Diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_NO_ROOT_GRAMMAR);
    }

    [Fact]
    public void Build_MultipleGrammarDeclarations_EmitsMultipleRootGrammarsDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"other.storm\";\ntoken Foo = \"foo\";",
            ["root/other.storm"] = "grammar Other;\ntoken Bar = \"bar\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.False(grammar.Succeeded);
        Assert.Contains(grammar.Diagnostics, d =>
            d.Id == StormDiagnosticIds.STORM_MULTIPLE_ROOT_GRAMMARS);
    }

    [Fact]
    public void Build_GrammarDeclaration_StoresGrammarName()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar MyLang;\ntoken Foo = \"foo\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.Equal("MyLang", grammar.GrammarName);
    }

    [Fact]
    public void Build_GrammarDeclaration_OnlyInRoot_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["root/main.storm"] = "grammar Test;\nimport \"tokens.storm\";\nrule Start = Foo;",
            ["root/tokens.storm"] = "token Foo = \"foo\";",
        };

        var grammar = BuildGrammar(files, "root/main.storm");

        Assert.True(grammar.Succeeded);
        Assert.Equal("Test", grammar.GrammarName);
    }
}