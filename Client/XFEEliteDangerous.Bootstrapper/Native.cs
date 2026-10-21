using System.Runtime.InteropServices;
using System.Text;

namespace XFEEliteDangerous.Bootstrapper;

internal static class Native
{
    internal const uint Suspended = 4, NoWindow = 0x08000000, ExtendedStartup = 0x00080000;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Startup
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;
        public short ShowWindow, ReservedSize; public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupEx { public Startup Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { public nint AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit
    { public long PerProcessTime, PerJobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimit
    { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("userenv", CharSet=CharSet.Unicode)] internal static extern int CreateAppContainerProfile(string name,string displayName,string description,nint capabilities,uint count,out nint sid);
    [DllImport("userenv", CharSet=CharSet.Unicode)] internal static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv", CharSet=CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string name,out nint sid);
    [DllImport("advapi32")] internal static extern nint FreeSid(nint sid);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list,int count,uint flags,ref nuint size);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list,uint flags,nuint attribute,nint value,nuint size,nint previous,nint returnSize);
    [DllImport("kernel32")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string app,StringBuilder command,nint processAttrs,nint threadAttrs,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint flags,nint environment,string directory,ref StartupEx startup,out ProcessInfo process);
    [DllImport("kernel32", CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateChild(string app,StringBuilder command,nint processAttrs,nint threadAttrs,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint flags,nint environment,string directory,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32", SetLastError=true)] internal static extern uint ResumeThread(nint thread);
    [DllImport("kernel32", SetLastError=true)] internal static extern uint WaitForSingleObject(nint handle,uint milliseconds);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process,out uint exitCode);
    [DllImport("kernel32")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(nint process,uint exitCode);
    [DllImport("kernel32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadProcessMemory(nint process,nint source,byte[] bytes,nuint size,out nuint read);
    [DllImport("kernel32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteProcessMemory(nint process,nint target,byte[] bytes,nuint size,out nuint written);
    [DllImport("kernel32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualProtectEx(nint process,nint address,nuint size,uint protection,out uint old);
    [DllImport("kernel32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushInstructionCache(nint process,nint address,nuint size);
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessBasicInformation { public nint Reserved1,Peb,Reserved2A,Reserved2B,UniqueId,Reserved3; }
    [DllImport("ntdll")] internal static extern int NtQueryInformationProcess(nint process,int informationClass,ref ProcessBasicInformation information,uint length,out uint returned);
    [DllImport("ntdll")] internal static extern int NtQueryInformationThread(nint thread,int informationClass,out uint value,uint length,out uint returned);
    [DllImport("kernel32")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateJobObjectW(nint attributes,string? name);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(nint job,int informationClass,ref ExtendedLimit limits,uint length);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(nint job,nint process);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessInJob(nint process,nint job,[MarshalAs(UnmanagedType.Bool)] out bool result);
    [StructLayout(LayoutKind.Sequential)] internal struct JobAccounting { public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime; public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses; }
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(nint job,int informationClass,out JobAccounting accounting,uint length,nint returned);
    [DllImport("advapi32", CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,uint revision,out nint descriptor,out uint size);
    [DllImport("kernel32")] internal static extern nint LocalFree(nint memory);
    [DllImport("kernel32", CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateNamedPipeW(string name,uint mode,uint pipeMode,uint maxInstances,uint outBuffer,uint inBuffer,uint timeout,ref SecurityAttributes attrs);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ConnectNamedPipe(nint pipe,nint overlapped);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(nint pipe,out uint processId);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadFile(nint file,byte[] bytes,uint count,out uint read,nint overlapped);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteFile(nint file,byte[] bytes,uint count,out uint written,nint overlapped);
    [DllImport("kernel32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelIoEx(nint file,nint overlapped);
    [DllImport("advapi32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(nint token,int informationClass,nint buffer,uint length,out uint needed);
    [DllImport("kernel32")] internal static extern nint GetCurrentProcess();
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateFileW(string path,uint access,uint share,nint attributes,uint creation,uint flags,nint template);
    [DllImport("kernel32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetFileInformationByHandle(nint file,int informationClass,ref uint information,uint size);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateDirectoryW(string path,nint attributes);
    [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateDesktopW(string name,nint device,nint devmode,uint flags,uint access,ref SecurityAttributes attributes);
    [DllImport("user32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate bool EnumWindowsCallback(nint window,nint parameter);
    [DllImport("user32",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDesktopWindows(nint desktop,EnumWindowsCallback callback,nint parameter);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint window,out uint processId);
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int GetClassNameW(nint window,StringBuilder name,int length);
    [DllImport("user32")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32")] internal static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformationW(nint handle,int index,StringBuilder name,uint length,out uint needed);
    [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateWindowExW(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetAppContainerNamedObjectPath(nint token,nint sid,uint length,StringBuilder? path,out uint needed);
}
