using System;
using System.Collections.Generic;
using System.Linq;
using Storm.SourceGenerator.IR;

namespace Storm.SourceGenerator.Emit;

public static partial class ParserEmitter
{
    private sealed class Slot
    {
        private const string ListPrefix = "ImmutableArray<";

        public string Target { get; }
        public string Local { get; }
        public string TypeName { get; }
        public string? ElementType { get; }
        public string? Builder { get; }

        public Slot(string target, string local, string typeName, string builderSuffix)
        {
            Target = target;
            Local = local;
            TypeName = typeName;
            if(typeName.StartsWith(ListPrefix, StringComparison.Ordinal))
            {
                ElementType = typeName.Substring(ListPrefix.Length, typeName.Length - ListPrefix.Length - 1);
                Builder = local + builderSuffix;
            }
        }

        public string Value => Builder == null ? Local : Builder + ".ToImmutable()";

        public string Declaration => Builder != null
            ? $"var {Builder} = ImmutableArray.CreateBuilder<{ElementType}>();"
            : $"{TypeName} {Local} = {(TypeName.EndsWith("?", StringComparison.Ordinal) ? "null" : "default")};";
    }

    private readonly record struct Feed(Slot Slot, bool Appends);

    private sealed class GroupPlan
    {
        public string StructName { get; }
        public string Prefix { get; }
        public IReadOnlyList<FieldLabelIR> Labels { get; }
        public IReadOnlyList<GroupStructFieldDescriptor> Fields { get; }

        public GroupPlan(string structName, string qualifiedName, string prefix, SequenceIR sequence, IReadOnlyList<FieldLabelIR> labels)
        {
            StructName = qualifiedName;
            Prefix = prefix;
            Labels = labels;
            Fields = AstEmitter.GetGroupStructFields(structName, sequence, labels);
        }
    }

    private sealed class Frame
    {
        private readonly Frame? parent;
        private readonly Dictionary<ExpressionIR, List<Feed>> feeds = new();

        public Frame(Frame? parent)
        {
            this.parent = parent;
        }

        public List<Slot> Slots { get; } = new();

        public Dictionary<SequenceIR, GroupPlan> Groups { get; } = new();

        public void Feed(ExpressionIR source, Slot slot, bool appends)
        {
            if(!feeds.TryGetValue(source, out var list))
            {
                list = new List<Feed>();
                feeds[source] = list;
            }

            list.Add(new Feed(slot, appends));
        }

        public IEnumerable<Feed> FeedsOf(ExpressionIR source)
        {
            var own = feeds.TryGetValue(source, out var list) ? list : Enumerable.Empty<Feed>();
            return parent == null ? own : own.Concat(parent.FeedsOf(source));
        }

        public bool TryGroup(ExpressionIR expr, out SequenceIR sequence, out GroupPlan plan)
        {
            var inner = expr switch
            {
                OptionalIR optional => optional.Inner,
                ZeroOrMoreIR zeroOrMore => zeroOrMore.Inner,
                OneOrMoreIR oneOrMore => oneOrMore.Inner,
                _ => null,
            };

            if(inner is SequenceIR candidate && Groups.TryGetValue(candidate, out var found))
            {
                sequence = candidate;
                plan = found;
                return true;
            }

            sequence = null!;
            plan = null!;
            return false;
        }
    }

    private sealed class ElementWriter
    {
        private readonly CodeWriter writer;
        private readonly EmissionContext context;
        private int next;

        public ElementWriter(CodeWriter writer, EmissionContext context)
        {
            this.writer = writer;
            this.context = context;
        }

        public Frame DeclareNode(IReadOnlyList<NodeSlot> slots, RuleAlternativeIR alternative, string nodeTypeName, IEnumerable<string>? takenLocals = null)
        {
            var frame = new Frame(null);
            var groupLocals = new Dictionary<SequenceIR, string>();
            foreach(var group in AstEmitter.DetectAnonymousGroups(alternative))
            {
                var structName = nodeTypeName + "Group" + group.GroupIndex;
                frame.Groups[group.Sequence] = new GroupPlan(structName, structName, "_g" + group.GroupIndex, group.Sequence, group.Labels);
                groupLocals[group.Sequence] = "_group" + group.GroupIndex;
            }

            var used = new HashSet<string>(takenLocals ?? Enumerable.Empty<string>());
            foreach(var nodeSlot in slots)
            {
                var isGroup = nodeSlot.Source is SequenceIR sequence && groupLocals.ContainsKey(sequence);
                var local = isGroup ? groupLocals[(SequenceIR)nodeSlot.Source] : "_" + nodeSlot.Name;
                while(used.Contains(local))
                {
                    local += "_";
                }

                var slot = new Slot(nodeSlot.Name, local, nodeSlot.TypeName, isGroup ? "_b" : "Builder");
                _ = used.Add(slot.Local);
                if(slot.Builder != null)
                {
                    _ = used.Add(slot.Builder);
                }

                writer.WriteLine(slot.Declaration);
                frame.Slots.Add(slot);
                frame.Feed(nodeSlot.Source, slot, nodeSlot.Appends);
            }

            return frame;
        }

        public void FinishLists(Frame frame)
        {
            foreach(var slot in frame.Slots.Where(slot => slot.Builder != null))
            {
                writer.WriteLine($"var {slot.Local} = {slot.Builder}.ToImmutable();");
            }
        }

        public void Element(ExpressionIR expr, string ok, Frame frame)
        {
            if(frame.TryGroup(expr, out var groupSequence, out var plan))
            {
                Group(expr, groupSequence, plan, ok, frame);
                return;
            }

            switch(expr)
            {
                case SequenceIR sequence:
                    foreach(var element in sequence.Elements)
                    {
                        Element(element, ok, frame);
                    }

                    break;

                case TokenRefIR tokenRef:
                    Leaf(expr, $"Match((int){TokenKindRef(tokenRef.Token.Name)})", tokenValued: true, ok, frame);
                    break;

                case StringLiteralIR literal:
                    var token = FindTokenForLiteral(context, literal.Value);
                    if(token == null)
                    {
                        writer.WriteLine($"{ok} = false;");
                    }
                    else
                    {
                        Leaf(expr, $"Match((int){TokenKindRef(token.Name)})", tokenValued: true, ok, frame);
                    }

                    break;

                case AlternationIR alternation:
                    var choice = "(" + string.Join(" ?? ", alternation.Alternatives.Select(alternative => MatchExprForAlternative(context, alternative))) + ")";
                    Leaf(expr, choice, tokenValued: true, ok, frame);
                    break;

                case RuleRefIR ruleRef:
                    Leaf(expr, $"_Parse{ruleRef.Rule.Name}()", tokenValued: false, ok, frame);
                    break;

                case PredicateIR predicate:
                    EmitPredicate(writer, context, predicate, ok);
                    break;

                case OptionalIR optional:
                    Optional(optional.Inner, ok, frame);
                    break;

                case ZeroOrMoreIR zeroOrMore:
                    Repeat(zeroOrMore.Inner, atLeastOnce: false, ok, frame);
                    break;

                case OneOrMoreIR oneOrMore:
                    Repeat(oneOrMore.Inner, atLeastOnce: true, ok, frame);
                    break;

                default:
                    throw new InvalidOperationException($"Lowering refuses a {expr.GetType().Name} in a rule body");
            }
        }

        private void Leaf(ExpressionIR source, string match, bool tokenValued, string ok, Frame frame)
        {
            var feeds = frame.FeedsOf(source).ToList();
            if(feeds.Count == 0)
            {
                writer.WriteLine($"if ({ok} && {match} == null) {ok} = false;");
                return;
            }

            var value = "_m" + next++;
            writer.WriteLine($"var {value} = {ok} ? {match} : null;");
            writer.WriteBlock($"if ({value} == null)", () => writer.WriteLine($"{ok} = false;"));
            writer.WriteBlock("else", () =>
            {
                foreach(var feed in feeds)
                {
                    var unwrap = tokenValued && (feed.Appends ? feed.Slot.ElementType : feed.Slot.TypeName) == "Token";
                    Store(feed, unwrap ? value + ".Value" : value);
                }
            });
        }

        private void Store(Feed feed, string value)
        {
            writer.WriteLine(feed.Appends
                ? $"{feed.Slot.Builder}.Add({value});"
                : $"{feed.Slot.Local} = {value};");
        }

        private void Optional(ExpressionIR inner, string ok, Frame frame)
        {
            writer.WriteBlock($"if ({ok})", () =>
            {
                var attempt = Save(Fed([inner], frame));
                writer.WriteLine($"var {attempt.Ok} = true;");
                Element(inner, attempt.Ok, frame);
                writer.WriteBlock($"if (!{attempt.Ok} || _hasError)", () => Restore(attempt));
            });
        }

        private void Repeat(ExpressionIR inner, bool atLeastOnce, string ok, Frame frame)
        {
            var count = "_count" + next++;
            if(atLeastOnce)
            {
                writer.WriteLine($"var {count} = 0;");
            }

            writer.WriteBlock($"while ({ok} && !_hasError)", () =>
            {
                var round = Save(Fed([inner], frame));
                writer.WriteLine($"var {round.Ok} = true;");
                Element(inner, round.Ok, frame);
                writer.WriteBlock($"if (!{round.Ok} || _hasError || _pos == {round.Position})", () =>
                {
                    Restore(round);
                    writer.WriteLine("break;");
                });

                if(atLeastOnce)
                {
                    writer.WriteLine($"{count}++;");
                }
            });

            if(atLeastOnce)
            {
                writer.WriteLine($"if ({count} == 0) {ok} = false;");
            }
        }

        private void Group(ExpressionIR wrapper, SequenceIR sequence, GroupPlan plan, string ok, Frame frame)
        {
            var groupOk = plan.Prefix + "Ok";
            if(wrapper is OptionalIR)
            {
                writer.WriteBlock($"if ({ok})", () =>
                {
                    var attempt = Save(Fed(sequence.Elements, frame));
                    writer.WriteLine($"var {groupOk} = true;");
                    var value = GroupBody(sequence, plan, groupOk, frame);
                    writer.WriteBlock($"if ({groupOk} && !_hasError)", () => StoreGroup(sequence, value, frame));
                    writer.WriteBlock($"if (!{groupOk} || _hasError)", () => Restore(attempt));
                });
                return;
            }

            var count = "_count" + next++;
            var atLeastOnce = wrapper is OneOrMoreIR;
            if(atLeastOnce)
            {
                writer.WriteLine($"var {count} = 0;");
            }

            writer.WriteBlock($"while ({ok} && !_hasError)", () =>
            {
                var round = Save(Fed(sequence.Elements, frame));
                writer.WriteLine($"var {groupOk} = true;");
                var value = GroupBody(sequence, plan, groupOk, frame);
                writer.WriteBlock($"if (!{groupOk} || _hasError || _pos == {round.Position})", () =>
                {
                    Restore(round);
                    writer.WriteLine("break;");
                });

                StoreGroup(sequence, value, frame);
                if(atLeastOnce)
                {
                    writer.WriteLine($"{count}++;");
                }
            });

            if(atLeastOnce)
            {
                writer.WriteLine($"if ({count} == 0) {ok} = false;");
            }
        }

        private string GroupBody(SequenceIR sequence, GroupPlan plan, string groupOk, Frame parent)
        {
            var frame = new Frame(parent);
            foreach(var field in plan.Fields)
            {
                var nestedStructName = field.Kind == GroupFieldKind.NestedGroup ? plan.StructName + "." + field.NestedStructName : null;
                var fieldType = nestedStructName == null ? field.FieldType : field.FieldType.Replace(field.NestedStructName!, nestedStructName);
                var slot = new Slot(field.FieldName, plan.Prefix + "_" + LowerFirst(field.FieldName), fieldType, "_b");
                writer.WriteLine(slot.Declaration);
                frame.Slots.Add(slot);
                switch(field.Kind)
                {
                    case GroupFieldKind.NestedGroup:
                        frame.Groups[field.NestedSequence!] = new GroupPlan(field.NestedStructName!, nestedStructName!, slot.Local, field.NestedSequence!, plan.Labels);
                        frame.Feed(field.NestedSequence!, slot, slot.Builder != null);
                        break;

                    case GroupFieldKind.Other:
                        if(AstEmitter.Representative(field.Element, plan.Labels, Array.Empty<ExpressionIR>()) is { } source)
                        {
                            frame.Feed(source, slot, slot.Builder != null);
                        }

                        break;

                    default:
                        frame.Feed(field.Element, slot, false);
                        break;
                }
            }

            foreach(var element in sequence.Elements)
            {
                Element(element, groupOk, frame);
            }

            var fields = string.Join(", ", frame.Slots.Select(slot => $"{slot.Target} = {slot.Value}"));
            return $"new {plan.StructName} {{ {fields} }}";
        }

        private void StoreGroup(SequenceIR sequence, string value, Frame frame)
        {
            foreach(var feed in frame.FeedsOf(sequence))
            {
                Store(feed, value);
            }
        }

        private List<Slot> Fed(IEnumerable<ExpressionIR> roots, Frame frame)
        {
            var slots = new List<Slot>();
            var pending = new Stack<ExpressionIR>(roots.Reverse());
            while(pending.Count > 0)
            {
                var expr = pending.Pop();
                foreach(var feed in frame.FeedsOf(expr))
                {
                    if(!slots.Contains(feed.Slot))
                    {
                        slots.Add(feed.Slot);
                    }
                }

                var children = expr switch
                {
                    SequenceIR sequence => sequence.Elements,
                    OptionalIR optional => [optional.Inner],
                    ZeroOrMoreIR zeroOrMore => [zeroOrMore.Inner],
                    OneOrMoreIR oneOrMore => [oneOrMore.Inner],
                    _ => Array.Empty<ExpressionIR>(),
                };
                foreach(var child in children.Reverse())
                {
                    pending.Push(child);
                }
            }

            return slots;
        }

        private Attempt Save(List<Slot> fed)
        {
            var id = next++;
            var attempt = new Attempt("_ok" + id, "_s" + id, "_e" + id, "_d" + id, fed.Select((slot, index) => (slot, "_v" + id + "_" + index)).ToList());
            writer.WriteLine($"var {attempt.Position} = _pos;");
            writer.WriteLine($"var {attempt.Error} = _hasError;");
            writer.WriteLine($"var {attempt.Diagnostics} = _diagnostics?.Count ?? 0;");
            foreach(var (slot, saved) in attempt.Saved)
            {
                writer.WriteLine(slot.Builder != null
                    ? $"var {saved} = {slot.Builder}.Count;"
                    : $"var {saved} = {slot.Local};");
            }

            return attempt;
        }

        private void Restore(Attempt attempt)
        {
            writer.WriteLine($"_pos = {attempt.Position};");
            writer.WriteLine($"_hasError = {attempt.Error};");
            writer.WriteLine($"_diagnostics?.TruncateTo({attempt.Diagnostics});");
            foreach(var (slot, saved) in attempt.Saved)
            {
                writer.WriteLine(slot.Builder != null
                    ? $"{slot.Builder}.Count = {saved};"
                    : $"{slot.Local} = {saved};");
            }
        }
    }

    private sealed record Attempt(string Ok, string Position, string Error, string Diagnostics, List<(Slot Slot, string Saved)> Saved);
}