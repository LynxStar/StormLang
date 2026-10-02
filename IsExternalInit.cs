// Polyfill to enable C# record types on netstandard2.0.
// The compiler requires this type to exist when using init-only setters.

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static class IsExternalInit { }