using System.Security.Cryptography;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>Durable account-scoped receipts of native journal uploads, separate from authoritative commander saves.</summary>
internal sealed class NativeJournalArchive(string workspace,string accountId)
{
    internal bool Store(ReadOnlySpan<byte> body)
    {
        if(!Guid.TryParseExact(accountId,"N",out _))throw new InvalidDataException("Invalid local journal account identity.");
        if(body.Length>8*1024*1024)throw new NativeDataException(413,"Native journal exceeds the local bound.");
        var directory=LocalPaths.Directory(Path.Combine(workspace,"native-journal",accountId));
        Directory.CreateDirectory(directory);LocalPaths.Directory(directory);
        var hash=SHA256.HashData(body);
        var path=LocalPaths.FilePath(Path.Combine(directory,Convert.ToHexStringLower(hash)+".bin"));
        if(File.Exists(path)){Verify(path,hash,body.Length);return false;}
        var pending=LocalPaths.FilePath(Path.Combine(directory,".pending-"+Guid.NewGuid().ToString("N")));
        try
        {
            using(var output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {output.Write(body);output.Flush(true);}
            try{File.Move(pending,path);}
            catch(IOException)when(File.Exists(path)){Verify(path,hash,body.Length);return false;}
            return true;
        }
        finally{if(File.Exists(pending))File.Delete(pending);}
    }
    private static void Verify(string path,byte[] expected,int length)
    {
        using var source=File.OpenRead(LocalPaths.FilePath(path));
        if(source.Length!=length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source),expected))
            throw new InvalidDataException("The local journal archive contains a damaged receipt; it was preserved.");
    }
}
