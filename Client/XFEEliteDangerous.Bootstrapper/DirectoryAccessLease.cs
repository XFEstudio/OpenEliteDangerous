using XFEEliteDangerous.Client;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Temporary read/execute access for the AppContainer of a locked private profile.</summary>
internal sealed class DirectoryAccessLease : IDisposable
{
    private readonly string path,originalSddl;
    private readonly FileSystemAccessRule grant;
    private bool disposed;
    internal bool RecoveredPreviousGrant { get; }
    internal bool OriginalAclRestored { get; private set; }
    internal bool OriginalAclRulesRestored { get; private set; }
    internal DirectoryAccessLease(VerifiedOdysseyImage installation,string appContainerSid,string backupPath)
        : this(installation.ProductDirectory,appContainerSid,backupPath) { }

    private DirectoryAccessLease(string directory,string appContainerSid,string backupPath)
    {
        using var update=AcquireUpdate();
        path=SafePaths.LocalAbsolute(directory); SafePaths.NoReparseAncestors(path);
        if(!Directory.Exists(path)) throw new DirectoryNotFoundException();
        if(!appContainerSid.StartsWith("S-1-15-2-",StringComparison.Ordinal)||appContainerSid.Split('-').Length!=11)
            throw new ArgumentException("A private AppContainer SID is required.");
        grant=new FileSystemAccessRule(new SecurityIdentifier(appContainerSid),FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow);
        var info=new DirectoryInfo(path);
        var security=info.GetAccessControl();
        var existing=ExplicitProfileRules(security).ToArray();
        if(existing.Length!=0)
        {
            // The caller holds the profile lock and created or derived its SID. A previous
            // launcher may have exited before disposing this exact inheritable grant.
            if(existing.Length!=1||!MatchesGrant(existing[0]))
                throw new IOException(AppText.Get("Launch.GameAccessConflict"));
            RecoveredPreviousGrant=true;
            security.RemoveAccessRuleSpecific(grant);
        }
        originalSddl=security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using(var backup=SafePaths.CreateNewReport(backupPath))
        {
            JsonSerializer.Serialize(backup,new{kind="temporary-appcontainer-rx-acl-lease",directory=path,appContainerSid,
                originalAccessSddl=originalSddl,rights="ReadAndExecute",gameContentModified=false,recoveredPreviousGrant=RecoveredPreviousGrant});
            backup.Flush(true);
        }
        if(!RecoveredPreviousGrant)
        {
            security.AddAccessRule(grant);
            info.SetAccessControl(security);
        }
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true;
        using var update=AcquireUpdate();
        SafePaths.NoReparseAncestors(path);
        var info=new DirectoryInfo(path);
        var current=info.GetAccessControl();
        // Remove only the exact lease. Preserve later permission changes, including changes for this SID.
        if(ExplicitProfileRules(current).Any(MatchesGrant))
        {
            current.RemoveAccessRuleSpecific(grant);
            info.SetAccessControl(current);
        }
        var restored=info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        OriginalAclRestored=restored==originalSddl;
        // NTFS can canonicalize SE_DACL_AUTO_INHERITED without changing a single ACE or protection bit.
        OriginalAclRulesRestored=restored.Replace("D:AI","D:",StringComparison.Ordinal)==originalSddl.Replace("D:AI","D:",StringComparison.Ordinal);
    }

    private IEnumerable<FileSystemAccessRule> ExplicitProfileRules(DirectorySecurity security) => security
        .GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
        .Where(rule=>rule.IdentityReference.Equals(grant.IdentityReference));

    private bool MatchesGrant(FileSystemAccessRule rule) => rule.AccessControlType==grant.AccessControlType
        && rule.FileSystemRights==grant.FileSystemRights && rule.InheritanceFlags==grant.InheritanceFlags
        && rule.PropagationFlags==grant.PropagationFlags;

    private sealed class AclUpdate : IDisposable
    {
        private readonly Mutex mutex=new(false,@"Local\XFEEliteDangerous.ProductDirectoryAcl");
        internal AclUpdate(){try{if(!mutex.WaitOne(TimeSpan.FromSeconds(15)))throw new TimeoutException(AppText.Get("DirectoryAccessLease.Text001"));}catch(AbandonedMutexException){}}
        public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
    }
    private static IDisposable AcquireUpdate()=>new AclUpdate();
}
