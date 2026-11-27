#if NETFRAMEWORK
// Records need this type, which .NET Framework does not define. The net10 target has it built in.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
