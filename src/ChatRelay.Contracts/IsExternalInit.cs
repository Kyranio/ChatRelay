// Polyfill required for `record` types and `init` setters on
// netstandard2.0. C# emits a synthetic IsExternalInit reference for those
// language features; the type only ships in net5+. Declaring it here as
// internal lets the compiler resolve it for any consumer of this assembly,
// including net48-targeting shells.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
