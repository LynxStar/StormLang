namespace Storm.SourceGenerator.IR;

/// <summary>
/// Abstract base class for mode transition actions on token declarations.
/// A token can push a new mode, pop the current mode, or have no mode action.
/// </summary>
public abstract class ModeActionIR;

/// <summary>
/// A push action that transitions the lexer to the specified mode.
/// </summary>
public sealed class PushModeIR : ModeActionIR
{
    /// <summary>
    /// The name of the mode to push onto the mode stack.
    /// </summary>
    public string ModeName { get; }

    public PushModeIR(string modeName)
    {
        ModeName = modeName;
    }
}

/// <summary>
/// A pop action that returns the lexer to the previous mode on the stack.
/// </summary>
public sealed class PopModeIR : ModeActionIR;

/// <summary>
/// No mode action — the lexer stays in the current mode after matching this token.
/// </summary>
public sealed class NoModeActionIR : ModeActionIR;