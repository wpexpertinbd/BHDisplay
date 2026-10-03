// Compiler support types that .NET Framework lacks (modern .NET has them built in).
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// Lets records (init-only properties) compile for .NET Framework.
    internal static class IsExternalInit { }
}
#endif

#if NETFRAMEWORK
namespace System.Collections.Generic
{
    internal static class KeyValuePairDeconstruct
    {
        /// `foreach (var (k, v) in dictionary)` — built in on modern .NET.
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
        { key = kv.Key; value = kv.Value; }
    }
}
#endif
