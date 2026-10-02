using System;

namespace Storm.SourceGenerator.Emit;

/// <summary>
/// An inclusive character range [Lo, Hi] used to define transitions
/// in NFA and DFA states. Supports single-character ranges and
/// multi-character ranges for character classes.
/// </summary>
internal readonly struct CharRange : IEquatable<CharRange>
{
    /// <summary>
    /// The inclusive lower bound of the range.
    /// </summary>
    public char Lo { get; }

    /// <summary>
    /// The inclusive upper bound of the range.
    /// </summary>
    public char Hi { get; }

    public CharRange(char lo, char hi)
    {
        Lo = lo;
        Hi = hi;
    }

    public CharRange(char single)
    {
        Lo = single;
        Hi = single;
    }

    /// <summary>
    /// Returns true if the character falls within this range (inclusive).
    /// </summary>
    public bool Contains(char c)
    {
        return c >= Lo && c <= Hi;
    }

    public bool Equals(CharRange other)
    {
        return Lo == other.Lo && Hi == other.Hi;
    }

    public override bool Equals(object? obj)
    {
        return obj is CharRange other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Lo.GetHashCode() * 397) ^ Hi.GetHashCode();
        }
    }

    public static bool operator ==(CharRange left, CharRange right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CharRange left, CharRange right)
    {
        return !left.Equals(right);
    }

    public override string ToString()
    {
        return Lo == Hi ? $"'{Lo}'" : $"['{Lo}'-'{Hi}']";
    }
}