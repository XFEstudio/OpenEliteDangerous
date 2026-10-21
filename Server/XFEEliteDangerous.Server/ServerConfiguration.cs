using XFEEliteDangerous.Client;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Server;

public sealed record ServerConfiguration(string Workspace,string DataRoot,int Port=48080,string? ArtifactsRoot=null,bool Background=false,OnlineServerOptions? Online=null)
{
    public string Address=>$"http://127.0.0.1:{Port}/api/";
    public static ServerConfiguration Load(string[] args)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Server");
        var values=new Dictionary<string,string>();
        for(var i=0;i<args.Length;i+=2)
            if(i+1>=args.Length||args[i] is not("--config" or "--workspace" or "--data-root" or "--port" or "--artifacts-root" or "--background")||!values.TryAdd(args[i],args[i+1]))throw new ArgumentException(AppText.Get("ServerConfiguration.Text461"));
        var file=values.GetValueOrDefault("--config")??(File.Exists(Path.Combine(root,"server.json"))?Path.Combine(root,"server.json"):null);
        var configuration=file is not null?JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(file),Wire.Json)??throw new InvalidDataException(AppText.Get("ServerConfiguration.Text462"))
            :new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","Offline"),root);
        configuration=configuration with{Workspace=Path.GetFullPath(values.GetValueOrDefault("--workspace")??configuration.Workspace),DataRoot=Path.GetFullPath(values.GetValueOrDefault("--data-root")??configuration.DataRoot),
            Port=values.TryGetValue("--port",out var port)?int.Parse(port,System.Globalization.CultureInfo.InvariantCulture):configuration.Port,
            ArtifactsRoot=values.GetValueOrDefault("--artifacts-root")??configuration.ArtifactsRoot,
            Background=values.TryGetValue("--background",out var background)?bool.Parse(background):configuration.Background};
        if(configuration.Port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(Port));
        return configuration;
    }
}
public sealed record LocalAdministratorAccess(string Address,string Key)
{
    public static LocalAdministratorAccess Open(string root,string address)
    {
        var file=Path.Combine(root,"admin-access.json");
        var access=File.Exists(file)?JsonSerializer.Deserialize<LocalAdministratorAccess>(File.ReadAllText(file),Wire.Json)??throw new InvalidDataException(AppText.Get("ServerConfiguration.Text463"))
            :new(address,Wire.Secret());
        if(access.Key.Length!=64||!access.Key.All(Uri.IsHexDigit))throw new InvalidDataException(AppText.Get("ServerConfiguration.Text463"));
        access=access with{Address=address};
        File.WriteAllText(file,JsonSerializer.Serialize(access,Wire.Json));
        if(OperatingSystem.IsWindows())
        {
            var security=new FileSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        else File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        return access;
    }
}
