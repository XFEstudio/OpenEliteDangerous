using System.Runtime.InteropServices;
using System.Text.Json;

namespace XFEEliteDangerous.Bootstrapper;

internal static class RuntimeChecks
{
    internal static unsafe int Run(string library,string workspace,string account,string logs)
    {
        Directory.CreateDirectory(logs);
        var module=NativeLibrary.Load(library);
        try
        {
            var hooks=(delegate* unmanaged<int>)NativeLibrary.GetExport(module,"XfeOfflineHookSelfTest");
            var hookResult=hooks();
            if(hookResult!=0)throw new InvalidOperationException("Owned x64 hook fixture failed.");
            var check=(delegate* unmanaged<byte*,int>)NativeLibrary.GetExport(module,"XfeOfflineDataSelfTest");
            var configuration=JsonSerializer.SerializeToUtf8Bytes(new { WorkspaceDirectory=workspace,AccountId=account,
                Executable=OwnedOfflineGame.Executable,ProfileRoot=logs,LogDirectory=logs,DriverVersion="" }).Concat(new byte[]{0}).ToArray();
            fixed(byte* pointer=configuration)
            {
                var result=check(pointer);
                if(result!=0)throw new InvalidOperationException("NativeAOT local data check failed: "+result+"; "+Path.Combine(logs,"data-self-test-error.txt"));
                return 0;
            }
        }
        // NativeAOT owns worker/runtime threads; keep the module mapped until this CLI process exits.
        finally { GC.KeepAlive(module); }
    }
}
