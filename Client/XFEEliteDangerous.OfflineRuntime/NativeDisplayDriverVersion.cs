using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XFEEliteDangerous.OfflineRuntime;

internal static unsafe partial class OfflineProfileHooks
{
    private static NativeHook? displayDriverHook;
    private static string? displayDriverVersion;
    private static delegate* unmanaged<char*,uint,byte> originalDisplayDriver;
    private static void InstallDisplayDriverVersion(nint image,string? actualVersion)
    {
        if(actualVersion is null)return;
        if(!ValidVersion(actualVersion))throw new InvalidDataException("Invalid actual display-driver metadata.");
        var entry=image+0x8C7B50;
        if(!new ReadOnlySpan<byte>((void*)entry,16).SequenceEqual(Convert.FromHexString("48895c24085556574154415541564157")))
            throw new InvalidDataException("The pinned display-driver WMI query differs.");
        displayDriverVersion=actualVersion;
        displayDriverHook=new NativeHook(entry,(nint)(delegate* unmanaged<char*,uint,byte>)&PrivateDisplayDriverVersion);
        originalDisplayDriver=(delegate* unmanaged<char*,uint,byte>)displayDriverHook.Trampoline;displayDriverHook.Enable();
    }
    private static byte CopyDisplayDriverVersion(char* buffer,uint capacity,string? value)
    {
        if(value is null||!ValidVersion(value))return 0;
        if(buffer==null||capacity==0)return 1;
        if(capacity>4096||capacity<=value.Length)return 0;
        value.AsSpan().CopyTo(new Span<char>(buffer,(int)capacity));buffer[value.Length]='\0';return 1;
    }
    [UnmanagedCallersOnly]private static byte PrivateDisplayDriverVersion(char* buffer,uint capacity)
        =>CopyDisplayDriverVersion(buffer,capacity,displayDriverVersion);

    private static bool ValidVersion(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiDigit(character) || character == '.') && value.Split('.').Length == 4;
}
