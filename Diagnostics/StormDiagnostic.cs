using System;
using Microsoft.CodeAnalysis;

namespace Storm.SourceGenerator.Diagnostics;

/// <summary>
/// Severity levels for Storm source-generator diagnostics.
/// </summary>
public enum StormDiagnosticSeverity
{
    /// <summary>A warning that does not prevent code generation.</summary>
    Warning,

    /// <summary>An error that prevents code generation and fails the build.</summary>
    Error,
}

/// <summary>
/// A diagnostic produced by the Storm source generator during grammar processing.
/// Carries enough information to be reported as a Roslyn build-time diagnostic.
/// </summary>
public sealed record StormDiagnostic(
    string Id,
    StormDiagnosticSeverity Severity,
    string Message,
    string? FilePath,
    int Line,
    int Column)
{
    private const string Category = "Storm.SourceGenerator";

    /// <summary>
    /// Creates a <see cref="DiagnosticDescriptor"/> suitable for Roslyn reporting.
    /// </summary>
    public DiagnosticDescriptor ToDescriptor()
    {
        return new DiagnosticDescriptor(
            id: Id,
            title: Id,
            messageFormat: "{0}",
            category: Category,
            defaultSeverity: ToRoslynSeverity(Severity),
            isEnabledByDefault: true);
    }

    /// <summary>
    /// Creates a Roslyn <see cref="Diagnostic"/> with an optional source location.
    /// When <see cref="FilePath"/> is non-null, the diagnostic includes a file-based location
    /// at the recorded line and column.
    /// </summary>
    public Diagnostic ToRoslynDiagnostic()
    {
        var descriptor = ToDescriptor();

        Location location;
        if(FilePath is not null)
        {
            var linePosition = new Microsoft.CodeAnalysis.Text.LinePosition(
                Math.Max(0, Line - 1),   // Roslyn LinePosition is 0-based
                Math.Max(0, Column - 1));
            var span = new Microsoft.CodeAnalysis.Text.LinePositionSpan(linePosition, linePosition);
            location = Location.Create(FilePath, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(0, 0), span);
        }
        else
        {
            location = Location.None;
        }

        return Diagnostic.Create(descriptor, location, Message);
    }

    /// <summary>
    /// Maps <see cref="StormDiagnosticSeverity"/> to Roslyn's <see cref="DiagnosticSeverity"/>.
    /// </summary>
    private static DiagnosticSeverity ToRoslynSeverity(StormDiagnosticSeverity severity)
    {
        return severity switch
        {
            StormDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            StormDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Error,
        };
    }
}