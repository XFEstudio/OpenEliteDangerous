using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Owns a private diagnostic desktop or a read-only lease on the explicitly requested visible desktop.
/// Never switches desktops, sends input or changes the shared desktop's security.</summary>
internal sealed class PrivateDesktopLease : IDisposable
{
    private nint handle;
    internal string Name { get; }
    internal string StartupName=>"winsta0\\"+Name;
    internal nint BorrowedHandle=>handle;
    internal bool IsSharedVisible {get;}
    internal PrivateDesktopLease(string appContainerSid,string id,bool visible=false)
    {
        if(!Guid.TryParseExact(id,"N",out _)) throw new ArgumentException("A random private desktop ID is required.");
        if(visible)
        {
            var current=Native.GetThreadDesktop(Native.GetCurrentThreadId());
            var name=new System.Text.StringBuilder(256);
            if(current==0||!Native.GetUserObjectInformationW(current,2,name,512,out _)
                ||!string.Equals(name.ToString(),"Default",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Visible startup requires the ordinary Default desktop.");
            Name=name.ToString();IsSharedVisible=true;
            // Parent inspection only: READOBJECTS|ENUMERATE. No ACL or integrity-label changes.
            handle=OpenDesktopW(Name,0,false,0x0001|0x0040);
            if(handle==0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return;
        }
        Name="XFEEliteDangerous_Diagnostic_"+id;
        var user=WindowsIdentity.GetCurrent().User?.Value??throw new InvalidOperationException("Missing caller SID.");
        // The app gets object/window creation, not SwitchDesktop, hooks or journal access.
        var descriptor=$"D:P(A;;GA;;;SY)(A;;GA;;;{user})(A;;0x000200c7;;;{appContainerSid})S:(ML;;NW;;;LW)";
        if(!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor,1,out var security,out _)) throw new Win32Exception();
        try
        {
            var attributes=new Native.SecurityAttributes{Length=Marshal.SizeOf<Native.SecurityAttributes>(),Descriptor=security};
            handle=Native.CreateDesktopW(Name,0,0,0,0x000f01ff,ref attributes);
            if(handle==0) throw new Win32Exception();
        }
        finally { Native.LocalFree(security); }
    }
    public void Dispose() { var value=Interlocked.Exchange(ref handle,0); if(value!=0&&!Native.CloseDesktop(value)) throw new Win32Exception(); }
    [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern nint OpenDesktopW(string name,uint flags,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint access);
}
