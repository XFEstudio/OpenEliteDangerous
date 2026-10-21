using XFEEliteDangerous.Client;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Temporary read/execute only access for one newly created AppContainer SID.</summary>
internal sealed class DirectoryAccessLease : IDisposable
{
    private readonly string path,sid,originalSddl;
    private bool disposed;
    internal bool OriginalAclRestored { get; private set; }
    internal bool OriginalAclRulesRestored { get; private set; }
    internal DirectoryAccessLease(string directory,string appContainerSid,string backupPath)
    {
        using var update=AcquireUpdate();
        path=SafePaths.LocalAbsolute(directory); SafePaths.NoReparseAncestors(path);
        if(!string.Equals(path,@"E:\Elite Dangerous",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("This temporary game lease is restricted to the explicitly authorized E diagnostic copy.");
        if(!Directory.Exists(path)) throw new DirectoryNotFoundException();
        sid=appContainerSid;
        if(!sid.StartsWith("S-1-15-2-",StringComparison.Ordinal)||sid=="S-1-15-2-1"||sid.Split('-').Length<10) throw new ArgumentException("A specific new AppContainer SID is required.");
        var info=new DirectoryInfo(path); var security=info.GetAccessControl();
        if(security.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(x=>x.IdentityReference.Value==sid)) throw new IOException("The random AppContainer SID already has explicit directory access.");
        originalSddl=security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using(var backup=SafePaths.CreateNewReport(backupPath)) JsonSerializer.Serialize(backup,new{kind="temporary-appcontainer-rx-acl-lease",directory=path,appContainerSid=sid,originalAccessSddl=originalSddl,rights="ReadAndExecute",gameContentModified=false});
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        info.SetAccessControl(security);
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true;
        using var update=AcquireUpdate();
        SafePaths.NoReparseAncestors(path);
        var info=new DirectoryInfo(path); var current=info.GetAccessControl();
        foreach(var rule in current.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(x=>x.IdentityReference.Value==sid).ToArray()) current.RemoveAccessRuleSpecific(rule);
        // Never replace the descriptor wholesale: concurrent changes for other principals survive.
        info.SetAccessControl(current);
        var restored=info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        OriginalAclRestored=restored==originalSddl;
        // NTFS can canonicalize SE_DACL_AUTO_INHERITED without changing a single ACE or protection bit.
        OriginalAclRulesRestored=restored.Replace("D:AI","D:",StringComparison.Ordinal)==originalSddl.Replace("D:AI","D:",StringComparison.Ordinal);
    }
    private sealed class AclUpdate : IDisposable
    {
        private readonly Mutex mutex=new(false,@"Local\XFEEliteDangerous.ECopyDirectoryAcl");
        internal AclUpdate(){try{if(!mutex.WaitOne(TimeSpan.FromSeconds(15)))throw new TimeoutException(AppText.Get("DirectoryAccessLease.Text001"));}catch(AbandonedMutexException){}}
        public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
    }
    private static IDisposable AcquireUpdate()=>new AclUpdate();
}
