using XFEEliteDangerous.Client;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Server;

public sealed record OnlineServerOptions(string BindAddress="0.0.0.0",int Port=48081,bool Enabled=true)
{
    public void Validate(){if(Port is <1024 or >65535||!IPAddress.TryParse(BindAddress,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)throw new InvalidDataException(AppText.Get("OnlineServerOptions.Text456"));}
    public X509Certificate2 OpenCertificate(string dataRoot)
    {
        Validate();var file=Path.Combine(dataRoot,"game-server.pfx");
        if(!File.Exists(file))
        {
            using var key=RSA.Create(3072);var request=new CertificateRequest("CN=XFEEliteDangerous private server",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
            var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
            using var created=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5));
            using var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(created.Export(X509ContentType.Pkcs12));output.Flush(true);
        }
        if(OperatingSystem.IsWindows())
        {
            var security=new FileSecurity();security.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WindowsIdentity.GetCurrent().User!,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        else File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var certificate=X509CertificateLoader.LoadPkcs12FromFile(file,null,OperatingSystem.IsWindows()?X509KeyStorageFlags.UserKeySet:X509KeyStorageFlags.EphemeralKeySet);
        var host=BindAddress=="0.0.0.0"?"localhost":BindAddress;
        var profile=new OnlineServerProfile($"https://{host}:{Port}/game/",Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)));
        File.WriteAllText(Path.Combine(dataRoot,"game-client.json"),JsonSerializer.Serialize(profile,GameLinkJson.Default.OnlineServerProfile));
        return certificate;
    }
}
