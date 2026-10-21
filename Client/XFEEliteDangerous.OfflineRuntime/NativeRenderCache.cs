using System.Runtime.InteropServices;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

internal static unsafe partial class OfflineProfileHooks
{
    private static delegate* unmanaged<char*,uint,uint,nint,uint,uint,nint,nint> originalCreateFile;
    private static delegate* unmanaged<uint> getFileError;
    private static delegate* unmanaged<uint,void> setFileError;
    private static string renderCacheSource="",renderCacheTarget="";

    private static void ConfigureRenderCache(string product,string profile)
    {
        renderCacheSource=Path.GetFullPath(Path.Combine(product,"GpuWorkTable.xml"));
        var folder=Path.Combine(profile,"RenderCache");Directory.CreateDirectory(folder);
        renderCacheTarget=Path.GetFullPath(Path.Combine(folder,"GpuWorkTable.xml"));
        if(renderCacheTarget.Length>=260)throw new InvalidDataException("The private render-cache path is too long.");
        var kernel=Native.GetModuleHandle("kernel32.dll");
        getFileError=(delegate* unmanaged<uint>)NativeLibrary.GetExport(kernel,"GetLastError");
        setFileError=(delegate* unmanaged<uint,void>)NativeLibrary.GetExport(kernel,"SetLastError");
    }

    // Only this image's CreateFileW import is routed. The exact product-root
    // GpuWorkTable.xml is the sole match; all access/share/disposition arguments
    // and the native last-error value retain their ordinary Win32 semantics.
    [UnmanagedCallersOnly]
    private static nint OpenWithRenderCache(char* path,uint access,uint share,nint security,uint creation,uint flags,nint template)
    {
        var previousError=getFileError==null?0:getFileError();nint replacement=0;var finalError=previousError;
        try
        {
            if(path!=null&&renderCacheSource.Length!=0)
            {
                var length=0;while(length<1024&&path[length]!=0)length++;
                if(length<1024)
                {
                    var text=new ReadOnlySpan<char>(path,length);
                    if(text.StartsWith("\\\\?\\",StringComparison.Ordinal))text=text[4..];
                    if(text.Equals(renderCacheSource, StringComparison.OrdinalIgnoreCase))
                    {
                        // An authentic shipped/client-generated table remains a
                        // read-only first-use fallback. New timings are written
                        // to the private cache and validated by the original GPU
                        // vendor/device/driver/task readers on the next launch.
                        var writes=(access&0x50010116u)!=0||creation!=3;
                        if(writes||File.Exists(renderCacheTarget))replacement=Marshal.StringToCoTaskMemUni(renderCacheTarget);
                    }
                }
            }
            if(setFileError!=null)setFileError(previousError);
            var handle=originalCreateFile(replacement==0?path:(char*)replacement,access,share,security,creation,flags,template);
            if(getFileError!=null)finalError=getFileError();
            if(replacement!=0&&handle==-1)
                OfflineDiagnostics.Current?.Write("warning","native.profile","render.cache.open.failed","Private render-cache open failed.",
                    new Dictionary<string,string?>{["win32Error"]=finalError.ToString(),["creationDisposition"]=creation.ToString()});
            return handle;
        }
        catch(Exception error){OfflineDiagnostics.Current?.Write("error","native.profile","render.cache.failed","Render-cache adapter failed.",exception:error);finalError=123;return -1;}
        finally
        {
            if(replacement!=0)Marshal.FreeCoTaskMem(replacement);
            if(setFileError!=null)setFileError(finalError);
        }
    }

    private static string fixtureCachePath="";private static uint fixtureCacheAccess,fixtureCacheShare,fixtureCacheCreation,fixtureCacheFlags;
    private static nint fixtureCacheSecurity,fixtureCacheTemplate;
    [UnmanagedCallersOnly]
    private static nint FixtureOpenCache(char* path,uint access,uint share,nint security,uint creation,uint flags,nint template)
    {
        fixtureCachePath=new string(path);fixtureCacheAccess=access;fixtureCacheShare=share;fixtureCacheSecurity=security;
        fixtureCacheCreation=creation;fixtureCacheFlags=flags;fixtureCacheTemplate=template;setFileError(183);return 0x1234;
    }

    [UnmanagedCallersOnly(EntryPoint="XfeEliteRenderCacheSelfTest")]
    public static ulong RenderCacheSelfTest()
    {
        var root=Path.Combine(Path.GetTempPath(),"XFEElite-render-cache-"+Guid.NewGuid().ToString("N"));
        var oldSource=renderCacheSource;var oldTarget=renderCacheTarget;var oldOpen=originalCreateFile;
        var oldGet=getFileError;var oldSet=setFileError;ulong passed=0;
        try
        {
            var product=Path.Combine(root,"product");var profile=Path.Combine(root,"profile");
            Directory.CreateDirectory(product);ConfigureRenderCache(product,profile);
            originalCreateFile=&FixtureOpenCache;
            var invoke=(delegate* unmanaged<char*,uint,uint,nint,uint,uint,nint,nint>)&OpenWithRenderCache;
            nint Call(string path,uint access=0x80000000,uint creation=3)
            {
                var pointer=Marshal.StringToCoTaskMemUni(path);
                try{return invoke((char*)pointer,access,7,0x55,creation,0x80,0x66);}finally{Marshal.FreeCoTaskMem(pointer);}
            }
            if(Call(renderCacheSource)==0x1234&&fixtureCachePath==renderCacheSource)passed|=1;
            if(Call(renderCacheSource,0x40000000,2)==0x1234&&fixtureCachePath==renderCacheTarget)passed|=2;
            if(fixtureCacheAccess==0x40000000&&fixtureCacheShare==7&&fixtureCacheSecurity==0x55&&fixtureCacheCreation==2&&fixtureCacheFlags==0x80&&fixtureCacheTemplate==0x66)passed|=4;
            File.WriteAllText(renderCacheTarget,"owned-fixture-cache");
            if(Call(renderCacheSource)==0x1234&&fixtureCachePath==renderCacheTarget)passed|=8;
            var other=Path.Combine(root,"different-product","GpuWorkTable.xml");
            if(Call(other,0x40000000,2)==0x1234&&fixtureCachePath==other)passed|=16;
            var ordinary=Path.Combine(product,"AppConfig.xml");
            if(Call(ordinary,0x40000000,2)==0x1234&&fixtureCachePath==ordinary)passed|=32;
            if(Call("\\\\?\\"+renderCacheSource.ToUpperInvariant())==0x1234&&fixtureCachePath==renderCacheTarget)passed|=64;
            fixed(char* path=renderCacheSource)
                if(invoke(path,0x80000000,7,0x55,3,0x80,0x66)==0x1234&&getFileError()==183)passed|=128;
            return passed;
        }
        catch{return passed;}
        finally
        {
            originalCreateFile=oldOpen;renderCacheSource=oldSource;renderCacheTarget=oldTarget;getFileError=oldGet;setFileError=oldSet;
            // Only these known fixture files/directories were created above.
            var file=Path.Combine(root,"profile","RenderCache","GpuWorkTable.xml");if(File.Exists(file))File.Delete(file);
            foreach(var directory in new[]{Path.Combine(root,"profile","RenderCache"),Path.Combine(root,"profile"),Path.Combine(root,"product"),root})
                if(Directory.Exists(directory))Directory.Delete(directory,false);
        }
    }
}
