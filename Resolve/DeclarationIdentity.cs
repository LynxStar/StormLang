using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Storm.SourceGenerator.Frontend;

namespace Storm.SourceGenerator.Resolve;

/// <summary>Declarations are records, and two files may hold equal ones, so a declaration's identity is its reference.</summary>
internal sealed class DeclarationIdentity : IEqualityComparer<StormDeclaration>
{
    internal static DeclarationIdentity Instance { get; } = new();

    public bool Equals(StormDeclaration? x, StormDeclaration? y)
    {
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(StormDeclaration obj)
    {
        return RuntimeHelpers.GetHashCode(obj);
    }
}