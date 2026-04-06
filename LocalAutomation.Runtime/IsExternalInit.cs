// Polyfill for netstandard2.1: the compiler requires this type for record class init-only setters,
// but it is only defined in net5.0+. Record structs do not need this polyfill because the compiler
// generates their init accessors differently.

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;
