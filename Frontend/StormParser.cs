using System;
using System.Collections.Generic;
using Storm.SourceGenerator.Diagnostics;

namespace Storm.SourceGenerator.Frontend;

/// <summary>
/// Hand-written recursive-descent parser for Storm grammar files.
/// Consumes a token list from <see cref="StormLexer"/> and produces
/// a <see cref="StormFile"/> AST node.
/// </summary>
public static class StormParser
{
    /// <summary>
    /// Parses a list of Storm tokens into a <see cref="StormFile"/> AST.
    /// </summary>
    /// <param name="tokens">The token list produced by <see cref="StormLexer.Tokenize"/>.</param>
    /// <param name="source">The source text (used for diagnostic locations).</param>
    /// <param name="diagnostics">Receives any parser diagnostics.</param>
    /// <returns>A <see cref="StormFile"/> representing the parsed grammar file.</returns>
    public static StormFile Parse(List<StormToken> tokens, StormSourceText source, List<StormDiagnostic> diagnostics)
    {
        if(tokens == null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if(source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if(diagnostics == null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        var state = new ParserState(tokens, source, diagnostics);
        return state.ParseFile();
    }

    /// <summary>
    /// Internal mutable parser state. Keeps the public API clean and static.
    /// </summary>
    private sealed class ParserState
    {
        private List<StormToken> tokens;
        private StormSourceText source;
        private List<StormDiagnostic> diagnostics;
        private int pos;

        public ParserState(List<StormToken> tokens, StormSourceText source, List<StormDiagnostic> diagnostics)
        {
            this.tokens = tokens;
            this.source = source;
            this.diagnostics = diagnostics;
            pos = 0;
        }

        // ----------------------------------------------------------
        //  Token access helpers
        // ----------------------------------------------------------

        private StormToken Current => pos < tokens.Count
            ? tokens[pos]
            : tokens[tokens.Count - 1]; // EOF sentinel

        private StormToken Peek(int offset = 0)
        {
            var idx = pos + offset;
            return idx < tokens.Count ? tokens[idx] : tokens[tokens.Count - 1];
        }

        private StormToken Advance()
        {
            var tok = Current;
            if(pos < tokens.Count - 1) // don't advance past EOF
            {
                pos++;
            }

            return tok;
        }

        private bool Check(StormTokenKind kind)
        {
            return Current.Kind == kind;
        }

        private bool Match(StormTokenKind kind)
        {
            if(Current.Kind == kind)
            {
                _ = Advance();
                return true;
            }

            return false;
        }

        private StormToken Expect(StormTokenKind kind, string expected)
        {
            if(Current.Kind == kind)
            {
                return Advance();
            }

            Error($"Expected {expected}, got '{Current.Lexeme}'");
            // Return a synthetic token so the caller can continue.
            return new StormToken(kind, "", Current.Offset, Current.Line, Current.Column);
        }

        private void Error(string message)
        {
            var tok = Current;
            diagnostics.Add(new StormDiagnostic(
                StormDiagnosticIds.STORM_SYNTAX_ERROR,
                StormDiagnosticSeverity.Error,
                message,
                source.FilePath,
                tok.Line,
                tok.Column));
        }

        // ----------------------------------------------------------
        //  Synchronization / recovery
        // ----------------------------------------------------------

        /// <summary>
        /// Skips tokens until we find one that could start a new
        /// top-level declaration or reach EOF.
        /// </summary>
        private void SynchronizeToDeclaration()
        {
            while(Current.Kind != StormTokenKind.EndOfFile)
            {
                switch(Current.Kind)
                {
                    case StormTokenKind.Annotation:
                    case StormTokenKind.RuleKeyword:
                    case StormTokenKind.TokenKeyword:
                    case StormTokenKind.FragmentKeyword:
                    case StormTokenKind.HiddenKeyword:
                    case StormTokenKind.TraitKeyword:
                    case StormTokenKind.ImportKeyword:
                    case StormTokenKind.GrammarKeyword:
                    case StormTokenKind.ModeKeyword:
                    case StormTokenKind.RightBrace:
                        return;
                    default:
                        _ = Advance();
                        break;
                }
            }
        }

        // ----------------------------------------------------------
        //  Top-level: File
        // ----------------------------------------------------------

        public StormFile ParseFile()
        {
            var imports = new List<StormImport>();
            var declarations = new List<StormDeclaration>();

            // Parse optional grammar declaration (must come before imports).
            StormGrammarDecl? grammarDecl = null;
            if(Current.Kind == StormTokenKind.GrammarKeyword)
            {
                grammarDecl = ParseGrammarDecl();
            }

            // Parse imports (must come before declarations).
            while(Current.Kind == StormTokenKind.ImportKeyword)
            {
                var imp = ParseImport();
                if(imp != null)
                {
                    imports.Add(imp);
                }
            }

            // Parse declarations until EOF.
            while(Current.Kind != StormTokenKind.EndOfFile)
            {
                var savedPos = pos;
                var decl = ParseDeclaration();
                if(decl != null)
                {
                    declarations.Add(decl);
                }

                // Guard against infinite loops: if no progress was made, consume one token.
                if(pos == savedPos)
                {
                    _ = Advance();
                }
            }

            return new StormFile(grammarDecl, imports, declarations);
        }

        // ----------------------------------------------------------
        //  Grammar declaration
        // ----------------------------------------------------------

        /// <summary>grammar Name ;</summary>
        private StormGrammarDecl ParseGrammarDecl()
        {
            var kw = Advance(); // consume 'grammar'
            var nameTok = Expect(StormTokenKind.Identifier, "grammar name");
            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormGrammarDecl(nameTok.Lexeme, kw.Offset, kw.Line, kw.Column);
        }

        // ----------------------------------------------------------
        //  Import declarations
        // ----------------------------------------------------------

        private StormImport? ParseImport()
        {
            // import "path" ;
            // import "path" as Namespace ;
            var importTok = Advance(); // consume 'import'
            var offset = importTok.Offset;
            var line = importTok.Line;
            var column = importTok.Column;

            if(Current.Kind != StormTokenKind.StringLiteral)
            {
                Error("Expected string literal after 'import'");
                SynchronizeToDeclaration();
                return null;
            }

            var pathTok = Advance();
            var path = UnquoteString(pathTok.Lexeme);


            //.storm is optional for paths, append it if missing.
            if(!path.EndsWith(".storm"))
            {
                path = $"{path}.storm";
            }

            string? alias = null;
            if(Match(StormTokenKind.AsKeyword))
            {
                if(Current.Kind != StormTokenKind.Identifier)
                {
                    Error("Expected namespace identifier after 'as'");
                    SynchronizeToDeclaration();
                    return new StormImport(path, null, offset, line, column);
                }

                alias = Advance().Lexeme;
            }

            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormImport(path, alias, offset, line, column);
        }

        // ----------------------------------------------------------
        //  Declarations
        // ----------------------------------------------------------

        private StormDeclaration? ParseDeclaration()
        {
            switch(Current.Kind)
            {
                case StormTokenKind.Annotation:
                    return ParseAnnotatedDeclaration();
                case StormTokenKind.RuleKeyword:
                    return ParseRuleDecl();
                case StormTokenKind.TokenKeyword:
                    return ParseTokenDecl();
                case StormTokenKind.FragmentKeyword:
                    return ParseFragmentDecl();
                case StormTokenKind.HiddenKeyword:
                    return ParseHiddenTokenDecl();
                case StormTokenKind.TraitKeyword:
                    return ParseTraitDecl();
                case StormTokenKind.ModeKeyword:
                    return ParseModeDecl();
                default:
                    Error($"Expected declaration (rule, token, fragment, or hidden), got '{Current.Lexeme}'");
                    SynchronizeToDeclaration();
                    return null;
            }
        }

        /// <summary>
        /// Parses an annotated declaration. Currently only @memo is recognized,
        /// and it must precede a rule declaration.
        /// </summary>
        private StormDeclaration? ParseAnnotatedDeclaration()
        {
            var annotationTok = Advance(); // consume the annotation token

            // Only @memo is recognized
            if(annotationTok.Lexeme != "@memo")
            {
                Error($"Unrecognized annotation '{annotationTok.Lexeme}'");
                SynchronizeToDeclaration();
                return null;
            }

            // Check for duplicate @memo
            if(Current.Kind == StormTokenKind.Annotation)
            {
                Error("Duplicate '@memo' annotation on rule declaration");
                _ = Advance(); // skip the duplicate
            }

            // @memo is only valid before 'rule'
            if(Current.Kind != StormTokenKind.RuleKeyword)
            {
                Error("'@memo' is only valid on rule declarations");
                SynchronizeToDeclaration();
                return null;
            }

            return ParseRuleDecl(isMemo: true);
        }

        /// <summary>rule Name (: Trait1, Trait2)? = expression ;</summary>
        private StormRuleDecl ParseRuleDecl(bool isMemo = false)
        {
            var kw = Advance(); // consume 'rule'
            var nameTok = Expect(StormTokenKind.Identifier, "rule name");

            // Optional trait bindings: : Trait1, Trait2
            List<string>? traitBindings = null;
            if(Match(StormTokenKind.Colon))
            {
                traitBindings = new List<string>();
                traitBindings.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                while(Match(StormTokenKind.Comma))
                {
                    traitBindings.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                }
            }

            _ = Expect(StormTokenKind.Equals, "'='");
            var body = ParseExpression();
            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormRuleDecl(nameTok.Lexeme, body, kw.Offset, kw.Line, kw.Column,
                                     IsMemo: isMemo, TraitBindings: traitBindings);
        }

        /// <summary>token Name = expression ( => push Identifier | => pop )? ;</summary>
        private StormTokenDecl ParseTokenDecl()
        {
            var kw = Advance(); // consume 'token'
            var nameTok = Expect(StormTokenKind.Identifier, "token name");
            _ = Expect(StormTokenKind.Equals, "'='");
            var body = ParseExpression();
            var action = ParseActionClause();
            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormTokenDecl(nameTok.Lexeme, body, kw.Offset, kw.Line, kw.Column, action);
        }

        /// <summary>fragment Name = expression ;</summary>
        private StormFragmentDecl ParseFragmentDecl()
        {
            var kw = Advance(); // consume 'fragment'
            var nameTok = Expect(StormTokenKind.Identifier, "fragment name");
            _ = Expect(StormTokenKind.Equals, "'='");
            var body = ParseExpression();
            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormFragmentDecl(nameTok.Lexeme, body, kw.Offset, kw.Line, kw.Column);
        }

        /// <summary>hidden token Name = expression ( => push Identifier | => pop )? ;</summary>
        private StormHiddenTokenDecl ParseHiddenTokenDecl()
        {
            var kw = Advance(); // consume 'hidden'
            _ = Expect(StormTokenKind.TokenKeyword, "'token'");
            var nameTok = Expect(StormTokenKind.Identifier, "token name");
            _ = Expect(StormTokenKind.Equals, "'='");
            var body = ParseExpression();
            var action = ParseActionClause();
            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormHiddenTokenDecl(nameTok.Lexeme, body, kw.Offset, kw.Line, kw.Column, action);
        }

        // ----------------------------------------------------------
        //  Mode declarations
        // ----------------------------------------------------------

        /// <summary>mode Identifier { Declaration* }</summary>
        private StormModeDecl ParseModeDecl()
        {
            var kw = Advance(); // consume 'mode'

            // Accept either an Identifier or the 'default' keyword as the mode name.
            string modeName;
            if(Current.Kind is StormTokenKind.Identifier or StormTokenKind.DefaultKeyword)
            {
                modeName = Advance().Lexeme;
            }
            else
            {
                Error($"Expected mode name after 'mode', got '{Current.Lexeme}'");
                modeName = "<error>";
            }

            if(Current.Kind != StormTokenKind.LeftBrace)
            {
                Error("Expected '{' after mode name");
                SynchronizeToDeclaration();
                return new StormModeDecl(modeName, new List<StormDeclaration>(), kw.Offset, kw.Line, kw.Column);
            }

            _ = Advance(); // consume '{'

            var declarations = new List<StormDeclaration>();
            while(Current.Kind is not StormTokenKind.RightBrace and not StormTokenKind.EndOfFile)
            {
                var decl = ParseDeclaration();
                if(decl != null)
                {
                    declarations.Add(decl);
                }
            }

            if(Current.Kind == StormTokenKind.RightBrace)
            {
                _ = Advance(); // consume '}'
            }
            else
            {
                Error("Unterminated mode block");
            }

            return new StormModeDecl(modeName, declarations, kw.Offset, kw.Line, kw.Column);
        }

        // ----------------------------------------------------------
        //  Trait declarations
        // ----------------------------------------------------------

        /// <summary>
        /// trait Name = field1:Type1 field2:Type2?;
        /// trait Name : ComposedTrait1, ComposedTrait2 = field1:Type1;
        /// </summary>
        private StormTraitDecl ParseTraitDecl()
        {
            var kw = Advance(); // consume 'trait'
            var nameTok = Expect(StormTokenKind.Identifier, "trait name");

            // Optional trait composition: : Trait1, Trait2
            var composedTraits = new List<string>();
            if(Match(StormTokenKind.Colon))
            {
                composedTraits.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                while(Match(StormTokenKind.Comma))
                {
                    composedTraits.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                }
            }

            // Fields are optional: trait Foo; or trait Foo : A, B;
            // When '=' is present, parse fields until semicolon
            var fields = new List<StormTraitField>();
            if(Match(StormTokenKind.Equals))
            {
                while(Current.Kind == StormTokenKind.Identifier && Peek(1).Kind == StormTokenKind.Colon)
                {
                    fields.Add(ParseTraitField());
                }
            }

            _ = Expect(StormTokenKind.Semicolon, "';'");
            return new StormTraitDecl(nameTok.Lexeme, fields, composedTraits,
                kw.Offset, kw.Line, kw.Column);
        }

        /// <summary>
        /// fieldName:TypeName(?|*|+)?
        /// </summary>
        private StormTraitField ParseTraitField()
        {
            var fieldNameTok = Advance(); // consume field name identifier
            _ = Advance(); // consume ':'
            var typeTok = Expect(StormTokenKind.Identifier, "type name");

            var cardinality = StormFieldCardinality.One;
            if(Match(StormTokenKind.Question))
            {
                cardinality = StormFieldCardinality.Optional;
            }
            else if(Match(StormTokenKind.Star))
            {
                cardinality = StormFieldCardinality.ZeroOrMore;
            }
            else if(Match(StormTokenKind.Plus))
            {
                cardinality = StormFieldCardinality.OneOrMore;
            }

            return new StormTraitField(fieldNameTok.Lexeme, typeTok.Lexeme, cardinality);
        }

        // ----------------------------------------------------------
        //  Action clauses
        // ----------------------------------------------------------

        /// <summary>
        /// Parses an optional action clause: => push Identifier | => pop
        /// Returns null if no action clause is present.
        /// </summary>
        private StormModeAction? ParseActionClause()
        {
            if(Current.Kind != StormTokenKind.FatArrow)
            {
                return null;
            }

            _ = Advance(); // consume '=>'

            if(Current.Kind == StormTokenKind.PushKeyword)
            {
                _ = Advance(); // consume 'push'

                // Accept Identifier or 'default' keyword as the mode name.
                if(Current.Kind is StormTokenKind.Identifier or StormTokenKind.DefaultKeyword)
                {
                    var modeNameTok = Advance();
                    return new StormPushAction(modeNameTok.Lexeme);
                }

                Error("Expected mode name after 'push'");
                return new StormPushAction("<error>");
            }

            if(Current.Kind == StormTokenKind.PopKeyword)
            {
                _ = Advance(); // consume 'pop'
                return new StormPopAction();
            }

            Error($"Expected 'push' or 'pop' after '=>', got '{Current.Lexeme}'");
            // Synchronize to the next semicolon or declaration keyword.
            SynchronizeToDeclaration();
            return null;
        }

        // ----------------------------------------------------------
        //  Expression parsing — recursive descent
        //
        //  Precedence (lowest to highest):
        //    1. Alternation  ( | )
        //    2. Named alternative  ( as Name )
        //    3. Sequence  (juxtaposition)
        //    4. Field label  ( name:element )
        //    5. Postfix  ( ? * + )
        //    6. Primary  (string, charclass, ref, qualified ref, group)
        // ----------------------------------------------------------

        /// <summary>
        /// Entry point for expression parsing. Parses alternation.
        /// </summary>
        private StormExpression ParseExpression()
        {
            return ParseAlternation();
        }

        /// <summary>
        /// Alternation: namedAlt ( '|' namedAlt )*
        /// </summary>
        private StormExpression ParseAlternation()
        {
            var first = ParseNamedAlternative();
            if(!Check(StormTokenKind.Pipe))
            {
                return first;
            }

            var alternatives = new List<StormExpression> { first };
            while(Match(StormTokenKind.Pipe))
            {
                alternatives.Add(ParseNamedAlternative());
            }

            return new StormAlternationExpr(alternatives);
        }

        /// <summary>
        /// Named alternative: sequence ( 'as' Name ( ':' Trait1, Trait2 )? )?
        /// </summary>
        private StormExpression ParseNamedAlternative()
        {
            var expr = ParseSequence();

            if(Check(StormTokenKind.AsKeyword))
            {
                _ = Advance(); // consume 'as'
                var nameTok = Expect(StormTokenKind.Identifier, "alternative name");

                // Optional trait bindings: : Trait1, Trait2
                List<string>? traitBindings = null;
                if(Match(StormTokenKind.Colon))
                {
                    traitBindings = new List<string>();
                    traitBindings.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                    while(Match(StormTokenKind.Comma))
                    {
                        traitBindings.Add(Expect(StormTokenKind.Identifier, "trait name").Lexeme);
                    }
                }

                return new StormNamedAlternative(expr, nameTok.Lexeme, traitBindings);
            }

            return expr;
        }

        /// <summary>
        /// Sequence: labeled+  (two or more → StormSequenceExpr, one → unwrap)
        /// </summary>
        private StormExpression ParseSequence()
        {
            var elements = new List<StormExpression>();
            while(IsExpressionStart())
            {
                elements.Add(ParseLabeled());
            }

            if(elements.Count == 0)
            {
                Error("Expected expression");
                // Return a placeholder so the caller can continue.
                return new StormReferenceExpr("<error>");
            }

            if(elements.Count == 1)
            {
                return elements[0];
            }

            return new StormSequenceExpr(elements);
        }

        /// <summary>
        /// Field label: Identifier ':' postfix  |  postfix
        /// </summary>
        private StormExpression ParseLabeled()
        {
            // Look ahead: if we see Identifier followed by ':', it's a label.
            if(Current.Kind == StormTokenKind.Identifier && Peek(1).Kind == StormTokenKind.Colon)
            {
                var labelTok = Advance(); // consume identifier
                _ = Advance(); // consume ':'
                var element = ParsePostfix();
                return new StormLabeledExpr(labelTok.Lexeme, element);
            }

            return ParsePredicate();
        }

        private StormExpression ParsePredicate()
        {
            if(Check(StormTokenKind.Ampersand) || Check(StormTokenKind.Bang))
            {
                var isNegated = Advance().Kind == StormTokenKind.Bang;
                return new StormPredicateExpr(ParsePostfix(), isNegated);
            }

            return ParsePostfix();
        }

        /// <summary>
        /// Postfix: primary ( '?' | '*' | '+' )*
        /// </summary>
        private StormExpression ParsePostfix()
        {
            var expr = ParsePrimary();

            while(true)
            {
                if(Match(StormTokenKind.Question))
                {
                    expr = new StormOptionalExpr(expr);
                }
                else if(Match(StormTokenKind.Star))
                {
                    expr = new StormZeroOrMoreExpr(expr);
                }
                else if(Match(StormTokenKind.Plus))
                {
                    expr = new StormOneOrMoreExpr(expr);
                }
                else
                {
                    break;
                }
            }

            return expr;
        }

        /// <summary>
        /// Primary: string literal | char class | reference | qualified reference | group
        /// </summary>
        private StormExpression ParsePrimary()
        {
            switch(Current.Kind)
            {
                case StormTokenKind.StringLiteral:
                    {
                        var tok = Advance();
                        return new StormStringLiteralExpr(UnquoteString(tok.Lexeme));
                    }

                case StormTokenKind.CharClass:
                    {
                        var tok = Advance();
                        return new StormCharClassExpr(tok.Lexeme);
                    }

                case StormTokenKind.RegexLiteral:
                    {
                        var tok = Advance();
                        var pattern = tok.Lexeme.Substring(1, tok.Lexeme.Length - 2);
                        return new StormRegexLiteralExpr(pattern);
                    }

                case StormTokenKind.Identifier:
                    {
                        var nameTok = Advance();
                        // Check for qualified reference: Name.Name
                        if(Check(StormTokenKind.Dot))
                        {
                            _ = Advance(); // consume '.'
                            var memberTok = Expect(StormTokenKind.Identifier, "qualified name");
                            return new StormQualifiedReferenceExpr(nameTok.Lexeme, memberTok.Lexeme);
                        }

                        return new StormReferenceExpr(nameTok.Lexeme);
                    }

                case StormTokenKind.InlineKeyword:
                    return ParseInlineBlock();

                case StormTokenKind.LeftParen:
                    {
                        _ = Advance(); // consume '('
                        var inner = ParseExpression();
                        _ = Expect(StormTokenKind.RightParen, "')'");
                        return new StormGroupExpr(inner);
                    }

                default:
                    {
                        Error($"Unexpected token '{Current.Lexeme}'");
                        _ = Advance(); // skip the bad token to avoid infinite loop
                        return new StormReferenceExpr("<error>");
                    }
            }
        }

        // ----------------------------------------------------------
        //  Inline block parsing
        // ----------------------------------------------------------

        /// <summary>
        /// Parses an inline block expression:
        /// <c>inline IDENTIFIER COLON LEFT_BRACE InlineArm (COMMA InlineArm)* COMMA? RIGHT_BRACE</c>
        /// </summary>
        private StormExpression ParseInlineBlock()
        {
            var inlineTok = Advance(); // consume 'inline'
            var offset = inlineTok.Offset;
            var line = inlineTok.Line;
            var column = inlineTok.Column;

            // Expect identifier (prefix name)
            if(Current.Kind != StormTokenKind.Identifier)
            {
                Error($"Expected prefix rule name after 'inline', got '{Current.Lexeme}'");
                SynchronizeToDeclaration();
                return new StormReferenceExpr("<error>");
            }

            var prefixTok = Advance();
            var prefix = new StormReferenceExpr(prefixTok.Lexeme);

            // Expect colon
            if(Current.Kind != StormTokenKind.Colon)
            {
                Error($"Expected ':' after prefix rule name '{prefixTok.Lexeme}', got '{Current.Lexeme}'");
                SynchronizeToDeclaration();
                return new StormReferenceExpr("<error>");
            }

            _ = Advance(); // consume ':'

            // Expect left brace
            if(Current.Kind != StormTokenKind.LeftBrace)
            {
                Error($"Expected '{{' after 'inline {prefixTok.Lexeme}:', got '{Current.Lexeme}'");
                SynchronizeToDeclaration();
                return new StormReferenceExpr("<error>");
            }

            _ = Advance(); // consume '{'

            // Parse comma-separated arms
            var arms = new List<StormInlineArm>();

            // Check for empty arm list
            if(Current.Kind == StormTokenKind.RightBrace)
            {
                Error("Inline block must contain at least one arm");
                _ = Advance(); // consume '}'
                return new StormInlineBlockExpr(prefix, arms, offset, line, column);
            }

            // Parse first arm
            var firstArm = ParseInlineArm();
            if(firstArm != null)
            {
                arms.Add(firstArm);
            }

            // Parse remaining arms separated by commas
            while(Match(StormTokenKind.Comma))
            {
                // Trailing comma before '}' is allowed
                if(Current.Kind == StormTokenKind.RightBrace)
                {
                    break;
                }

                var arm = ParseInlineArm();
                if(arm != null)
                {
                    arms.Add(arm);
                }
            }

            // Expect right brace
            if(Current.Kind == StormTokenKind.RightBrace)
            {
                _ = Advance(); // consume '}'
            }
            else
            {
                Error("Expected '}' to close inline block");
            }

            return new StormInlineBlockExpr(prefix, arms, offset, line, column);
        }

        /// <summary>
        /// Parses a single inline arm:
        /// <c>(IDENTIFIER COLON)? (InlineBlock | IDENTIFIER) AS IDENTIFIER</c>
        /// </summary>
        private StormInlineArm? ParseInlineArm()
        {
            string? label = null;
            StormExpression expression;

            // Check for optional label: IDENTIFIER COLON followed by an expression start
            // A label looks like "label:" — but we need to disambiguate from a plain identifier
            // followed by "as". The lookahead is: if we see IDENTIFIER COLON and after the colon
            // there's another IDENTIFIER or InlineKeyword, it's a label.
            if(Current.Kind == StormTokenKind.Identifier
                && Peek(1).Kind == StormTokenKind.Colon
                && (Peek(2).Kind == StormTokenKind.Identifier || Peek(2).Kind == StormTokenKind.InlineKeyword))
            {
                var labelTok = Advance(); // consume label identifier
                _ = Advance(); // consume ':'
                label = labelTok.Lexeme;
            }

            // Parse inner expression: nested inline block or identifier (rule reference)
            if(Current.Kind == StormTokenKind.InlineKeyword)
            {
                expression = ParseInlineBlock();
            }
            else if(Current.Kind == StormTokenKind.Identifier)
            {
                var refTok = Advance();
                expression = new StormReferenceExpr(refTok.Lexeme);
            }
            else
            {
                Error($"Expected rule reference or nested inline block in arm, got '{Current.Lexeme}'");
                // Skip to next comma or right brace to recover
                SynchronizeToArmBoundary();
                return null;
            }

            // Expect 'as'
            if(Current.Kind != StormTokenKind.AsKeyword)
            {
                Error($"Expected 'as' after rule reference in inline arm, got '{Current.Lexeme}'");
                // Skip to next comma or right brace to recover
                SynchronizeToArmBoundary();
                return null;
            }

            _ = Advance(); // consume 'as'

            // Expect alternative name identifier
            var altNameTok = Expect(StormTokenKind.Identifier, "alternative name");
            return new StormInlineArm(expression, altNameTok.Lexeme, label);
        }

        /// <summary>
        /// Skips tokens until we find a comma or right brace (arm boundary),
        /// allowing recovery from malformed inline arms.
        /// </summary>
        private void SynchronizeToArmBoundary()
        {
            while(Current.Kind != StormTokenKind.EndOfFile)
            {
                if(Current.Kind is StormTokenKind.Comma or StormTokenKind.RightBrace)
                {
                    return;
                }

                _ = Advance();
            }
        }

        // ----------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------

        /// <summary>
        /// Returns true if the current token can start an expression element.
        /// Used by sequence parsing to know when to stop.
        /// </summary>
        private bool IsExpressionStart()
        {
            return Current.Kind switch
            {
                StormTokenKind.StringLiteral or StormTokenKind.CharClass or StormTokenKind.RegexLiteral or StormTokenKind.Identifier or StormTokenKind.LeftParen or StormTokenKind.InlineKeyword or StormTokenKind.Ampersand or StormTokenKind.Bang => true,
                _ => false,
            };
        }

        /// <summary>
        /// Strips the surrounding double quotes from a string literal lexeme.
        /// Handles basic escape sequences.
        /// </summary>
        private static string UnquoteString(string lexeme)
        {
            if(lexeme.Length < 2 || lexeme[0] != '"')
            {
                return lexeme;
            }

            // Strip quotes
            var inner = lexeme.Substring(1, lexeme.Length - 2);

            // Process escape sequences
            if(inner.IndexOf('\\') < 0)
            {
                return inner;
            }

            var sb = new System.Text.StringBuilder(inner.Length);
            for(var i = 0; i < inner.Length; i++)
            {
                if(inner[i] == '\\' && i + 1 < inner.Length)
                {
                    var next = inner[i + 1];
                    switch(next)
                    {
                        case 'n': _ = sb.Append('\n'); break;
                        case 'r': _ = sb.Append('\r'); break;
                        case 't': _ = sb.Append('\t'); break;
                        case '\\': _ = sb.Append('\\'); break;
                        case '"': _ = sb.Append('"'); break;
                        default:
                            _ = sb.Append('\\');
                            _ = sb.Append(next);
                            break;
                    }

                    i++; // skip the escaped character
                }
                else
                {
                    _ = sb.Append(inner[i]);
                }
            }

            return sb.ToString();
        }
    }
}