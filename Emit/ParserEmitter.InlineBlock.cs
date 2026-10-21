using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

public static partial class ParserEmitter
{
    private static void EmitInlineBlock(CodeWriter writer, EmissionContext context, InlineBlockIR inlineBlock, string ruleName, string spVar)
    {
        new InlineEmission(writer, context, ruleName, spVar).Level(new InlineLevel(inlineBlock, null));
    }

    /// <summary>
    /// One inline block of a nest. A nested block's prefix rule begins with its parent's, which the parent has
    /// already parsed, so the level parses only <see cref="Own"/> and resolves the inherited elements to the
    /// instances an ancestor parsed, or to <see cref="Inherited"/>, which stands for the parent's prefix node.
    /// </summary>
    private sealed class InlineLevel
    {
        private readonly Dictionary<ExpressionIR, ExpressionIR> resolved = new();

        public InlineLevel(InlineBlockIR block, InlineLevel? parent)
        {
            Block = block;
            Parent = parent;
            Depth = parent == null ? 0 : parent.Depth + 1;
            var own = block.PrefixElements;
            if(parent != null && block.PrefixElements.FirstOrDefault() is RuleRefIR reference && reference.Rule == parent.Block.PrefixRule)
            {
                Inherited = reference;
                own = block.PrefixElements.Skip(1).ToList();
            }
            else if(parent != null)
            {
                var inherited = parent.Block.PrefixElements.Count;
                MapExpressions(block.PrefixElements.Take(inherited).ToList(), parent.Block.PrefixElements, resolved);
                ResolveAll(resolved, parent.Resolve);
                own = block.PrefixElements.Skip(inherited).ToList();
            }

            Own = own;
            OwnSources = Reachable(own);
            PrefixProjection = ProjectInlinePrefix(block, Resolve);
            foreach(var arm in block.Arms)
            {
                if(arm.TailExpression is InlineBlockIR nested)
                {
                    Children[arm] = new InlineLevel(nested, this);
                }
                else
                {
                    Arms[arm] = ProjectInlineArm(block, arm, Resolve);
                }
            }
        }

        public InlineBlockIR Block { get; }

        public InlineLevel? Parent { get; }

        public int Depth { get; }

        public RuleRefIR? Inherited { get; }

        public IReadOnlyList<ExpressionIR> Own { get; }

        public HashSet<ExpressionIR> OwnSources { get; }

        public InlineArmProjection PrefixProjection { get; }

        public Dictionary<InlineArmIR, InlineArmProjection> Arms { get; } = new();

        public Dictionary<InlineArmIR, InlineLevel> Children { get; } = new();

        public List<NodeSlot> Captures { get; } = new();

        public Frame Frame { get; set; } = null!;

        public string GroupPrefix => Depth == 0 ? "_inlineGroup" : Name("_inlineGroup") + "_";

        public IEnumerable<string> Locals => (Parent?.Locals ?? Enumerable.Empty<string>())
            .Concat(Frame.Slots.SelectMany(slot => new[] { slot.Local, slot.Builder }).OfType<string>());

        public string Name(string local)
        {
            return Depth == 0 ? local : local + Depth.ToString(CultureInfo.InvariantCulture);
        }

        public ExpressionIR Resolve(ExpressionIR expression)
        {
            return resolved.TryGetValue(expression, out var found) ? found : expression;
        }

        public IEnumerable<(InlineArmProjection Projection, string TypeName)> Consumers()
        {
            yield return (PrefixProjection, Block.PrefixRule.Name + "Node");
            foreach(var arm in Arms)
            {
                yield return (arm.Value, arm.Key.TargetRule.Name + "Node");
            }

            foreach(var consumer in Children.Values.SelectMany(child => child.Consumers()))
            {
                yield return consumer;
            }
        }

        public string? LocalOf(NodeSlot slot)
        {
            for(var i = 0; i < Captures.Count; i++)
            {
                if(Captures[i].Source == slot.Source && Captures[i].TypeName == slot.TypeName && Captures[i].Appends == slot.Appends)
                {
                    return Frame.Slots[i].Local;
                }
            }

            return Parent?.LocalOf(slot);
        }

        public InlineLevel? Supplier(ExpressionIR source)
        {
            return Inherited == source ? Parent : Parent?.Supplier(source);
        }
    }

    private sealed class InlineEmission
    {
        private readonly CodeWriter writer;
        private readonly EmissionContext context;
        private readonly ElementWriter elements;
        private readonly string ruleName;
        private readonly string spVar;

        public InlineEmission(CodeWriter writer, EmissionContext context, string ruleName, string spVar)
        {
            this.writer = writer;
            this.context = context;
            elements = new ElementWriter(writer, context);
            this.ruleName = ruleName;
            this.spVar = spVar;
        }

        public void Level(InlineLevel level)
        {
            var inherited = level.Parent?.Locals.ToList() ?? new List<string>();
            var frames = new List<Frame>();
            foreach(var (projection, typeName) in level.Consumers())
            {
                var captures = projection.Slots.Where(slot => level.OwnSources.Contains(slot.Source)).ToList();
                var taken = inherited.Concat(frames.SelectMany(frame => frame.Slots).SelectMany(slot => new[] { slot.Local, slot.Builder }).OfType<string>());
                frames.Add(elements.DeclareNode(captures, projection.Alternative, typeName, taken));
                level.Captures.AddRange(captures);
            }

            var ok = level.Name("_inlineOk");
            writer.WriteLine($"var {ok} = true;");
            level.Frame = MergeInlineFrames(frames, level.OwnSources, level.GroupPrefix);
            foreach(var element in level.Own)
            {
                elements.Element(element, ok, level.Frame);
            }

            elements.FinishLists(level.Frame);
            writer.WriteLine($"var {level.Name("_inlinePrefixEnd")} = _pos;");
            writer.WriteLine($"var {level.Name("_inlinePrefixSpan")} = Span({spVar});");

            writer.WriteBlock($"if ({ok} && !_hasError)", () => Dispatch(level));
        }

        private void Dispatch(InlineLevel level)
        {
            var armsByFirstToken = new Dictionary<string, List<int>>();
            var armsWithoutFirstToken = new List<int>();

            for(var i = 0; i < level.Block.Arms.Count; i++)
            {
                var firstTokens = FirstTokens(level, level.Block.Arms[i]);
                if(firstTokens.Count > 0)
                {
                    foreach(var token in firstTokens)
                    {
                        if(!armsByFirstToken.ContainsKey(token))
                        {
                            armsByFirstToken[token] = new List<int>();
                        }

                        armsByFirstToken[token].Add(i);
                    }
                }
                else
                {
                    armsWithoutFirstToken.Add(i);
                }
            }

            if(armsByFirstToken.Count == 0 && armsWithoutFirstToken.Count == 0)
            {
                return;
            }

            var peek = level.Name("_peek");
            writer.WriteLine($"var {peek} = Peek();");
            var isFirst = true;

            foreach(var kvp in armsByFirstToken)
            {
                var keyword = isFirst ? "if" : "else if";
                writer.WriteLine($"{keyword} ({peek} == (int){TokenKindRef(kvp.Key)})");
                writer.WriteLine("{");
                writer.Indent();

                ArmGroup(level, kvp.Value);

                writer.Dedent();
                writer.WriteLine("}");
                isFirst = false;
            }

            if(armsWithoutFirstToken.Count > 0)
            {
                if(!isFirst)
                {
                    writer.WriteLine("else");
                    writer.WriteLine("{");
                    writer.Indent();
                }

                ArmGroup(level, armsWithoutFirstToken);

                if(!isFirst)
                {
                    writer.Dedent();
                    writer.WriteLine("}");
                }
            }
        }

        private List<string> FirstTokens(InlineLevel level, InlineArmIR arm)
        {
            if(!level.Children.TryGetValue(arm, out var child))
            {
                return GetFirstTokensFromTail(arm.TailExpression, context);
            }

            if(child.Own.Count > 0)
            {
                return GetFirstTokensFromTail(child.Own[0], context);
            }

            var tokens = new List<string>();
            foreach(var nested in child.Block.Arms)
            {
                var nestedTokens = FirstTokens(child, nested);
                if(nestedTokens.Count == 0)
                {
                    return nestedTokens;
                }

                tokens.AddRange(nestedTokens.Where(token => !tokens.Contains(token)));
            }

            return tokens;
        }

        private void ArmGroup(InlineLevel level, List<int> armIndices)
        {
            if(armIndices.Count == 1)
            {
                ArmTail(level, level.Block.Arms[armIndices[0]]);
                return;
            }

            var saved = level.Name("_armSaved");
            var savedError = level.Name("_armSavedError");
            var savedDiagCount = level.Name("_armSavedDiagCount");
            writer.WriteLine($"var {saved} = _pos;");
            writer.WriteLine($"var {savedError} = _hasError;");
            writer.WriteLine($"var {savedDiagCount} = _diagnostics?.Count ?? 0;");

            for(var i = 0; i < armIndices.Count; i++)
            {
                var arm = level.Block.Arms[armIndices[i]];
                if(i > 0)
                {
                    writer.WriteEmptyLine();
                    writer.WriteLine($"// Try next arm: {arm.AlternativeName}");
                    writer.WriteLine($"_pos = {saved};");
                    writer.WriteLine($"_hasError = {savedError};");
                    writer.WriteLine($"_diagnostics?.TruncateTo({savedDiagCount});");
                }

                writer.WriteLine("{");
                writer.Indent();
                ArmTail(level, arm);
                writer.Dedent();
                writer.WriteLine("}");
            }
        }

        /// <summary>Builds the node shape <see cref="AstEmitter.EmitInlineArmNode"/> declares.</summary>
        private void ArmTail(InlineLevel level, InlineArmIR arm)
        {
            if(level.Children.TryGetValue(arm, out var child))
            {
                Level(child);
                return;
            }

            var armTypeName = arm.AlternativeName + ruleName + "Node";
            var innerTypeName = arm.TargetRule.Name + "Node";
            var armPropName = PascalCase(arm.Label ?? arm.TargetRule.Name);

            var projection = level.Arms[arm];
            var slots = projection.Slots;
            var tailSlots = slots.Where(slot => level.LocalOf(slot) == null && slot.Source != projection.PrefixReference && level.Supplier(slot.Source) == null).ToList();

            var tailOk = level.Name("_tailOk");
            writer.WriteLine($"var {tailOk} = true;");
            var tail = elements.DeclareNode(tailSlots, projection.Alternative, innerTypeName, level.Locals);
            elements.Element(arm.TailExpression, tailOk, tail);
            elements.FinishLists(tail);

            writer.WriteBlock($"if ({tailOk} && !_hasError)", () =>
            {
                var built = new Dictionary<InlineLevel, string>();
                var prefixNode = projection.PrefixReference == null ? null : NodeOf(level, built);
                var values = new List<string>();
                foreach(var slot in slots)
                {
                    values.Add(slot.Source == projection.PrefixReference ? prefixNode! : ValueOf(level, slot, built) ?? tail.Slots[tailSlots.IndexOf(slot)].Local);
                }

                var inner = level.Name("_inner");
                writer.WriteLine($"var {inner} = new {innerTypeName}();");
                for(var i = 0; i < slots.Count; i++)
                {
                    writer.WriteLine($"{inner}.{slots[i].Name} = {values[i]};");
                }

                writer.WriteLine($"{inner}.Span = Span({spVar});");

                var node = level.Name("node");
                writer.WriteLine($"var {node} = new {armTypeName}();");
                writer.WriteLine($"{node}.{armPropName} = {inner};");
                writer.WriteLine($"{node}.Span = Span({spVar});");
                writer.WriteLine($"{node} = AttachHidden({node}, {spVar}, _pos);");
                writer.WriteLine($"return {node};");
            });
        }

        private string? ValueOf(InlineLevel level, NodeSlot slot, Dictionary<InlineLevel, string> built)
        {
            return level.LocalOf(slot) ?? (level.Supplier(slot.Source) is { } supplier ? NodeOf(supplier, built) : null);
        }

        private string NodeOf(InlineLevel level, Dictionary<InlineLevel, string> built)
        {
            if(built.TryGetValue(level, out var existing))
            {
                return existing;
            }

            var slots = level.PrefixProjection.Slots;
            var values = new List<string>();
            foreach(var slot in slots)
            {
                values.Add(ValueOf(level, slot, built)
                    ?? throw new InvalidOperationException($"No parsed value feeds '{slot.Name}' of the prefix rule '{level.Block.PrefixRule.Name}'."));
            }

            var name = level.Name("_prefixNode");
            writer.WriteLine($"var {name} = new {level.Block.PrefixRule.Name}Node();");
            for(var i = 0; i < slots.Count; i++)
            {
                writer.WriteLine($"{name}.{slots[i].Name} = {values[i]};");
            }

            writer.WriteLine($"{name}.Span = {level.Name("_inlinePrefixSpan")};");
            writer.WriteLine($"{name} = AttachHidden({name}, {spVar}, {level.Name("_inlinePrefixEnd")});");
            built[level] = name;
            return name;
        }
    }

    private static HashSet<ExpressionIR> Reachable(IEnumerable<ExpressionIR> roots)
    {
        var reached = new HashSet<ExpressionIR>();
        var pending = new Stack<ExpressionIR>(roots);
        while(pending.Count > 0)
        {
            var expr = pending.Pop();
            if(!reached.Add(expr))
            {
                continue;
            }

            var children = expr switch
            {
                SequenceIR sequence => sequence.Elements,
                AlternationIR alternation => alternation.Alternatives,
                OptionalIR optional => [optional.Inner],
                ZeroOrMoreIR zeroOrMore => [zeroOrMore.Inner],
                OneOrMoreIR oneOrMore => [oneOrMore.Inner],
                PredicateIR predicate => [predicate.Inner],
                _ => Array.Empty<ExpressionIR>(),
            };
            foreach(var child in children)
            {
                pending.Push(child);
            }
        }

        return reached;
    }

    private static List<string> GetFirstTokensFromTail(ExpressionIR tailExpr, EmissionContext context)
    {
        var result = new List<string>();

        switch(tailExpr)
        {
            case TokenRefIR tokenRef:
                result.Add(tokenRef.Token.Name);
                break;
            case StringLiteralIR lit:
                var token = FindTokenForLiteral(context, lit.Value);
                if(token != null)
                {
                    result.Add(token.Name);
                }

                break;
            case SequenceIR seq:
                if(seq.Elements.Count > 0)
                {
                    return GetFirstTokensFromTail(seq.Elements[0], context);
                }

                break;
            default:
                break;
        }

        return result;
    }
}