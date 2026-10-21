using System.Management;
using System.Runtime.InteropServices;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Read only the display-driver metadata used by the pinned native query.</summary>
public static class DisplayDriverVersionReader
{
    public static Task<string?> ReadAsync(CancellationToken cancellation=default)=>Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            //8C7B50 enumerates Win32_VideoController, keeping the first non-Intel
            // Description (or the last Intel entry). Preserve that exact policy;
            // this metadata is not a guessed version or a cache-valid flag.
            using var search=new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT DriverVersion, Description FROM Win32_VideoController"),
                new System.Management.EnumerationOptions{Timeout=TimeSpan.FromSeconds(5),ReturnImmediately=true});
            using var rows=search.Get();string? last=null;
            foreach(ManagementObject row in rows)
            {
                using(row)
                {
                    cancellation.ThrowIfCancellationRequested();
                    last=row["DriverVersion"] as string;
                    var description=row["Description"] as string??"";
                    if(!description.Contains("Intel",StringComparison.Ordinal))break;
                }
            }
            return Valid(last)?last:null;
        }
        catch(Exception error) when(error is ManagementException or COMException or TimeoutException){return null;}
    },cancellation);

    private static bool Valid(string? value)=>value is {Length:>=7 and <=63}&&value.All(character=>char.IsAsciiDigit(character)||character=='.')
        &&Version.TryParse(value,out var version)&&version is {Major:>=0,Minor:>=0,Build:>=0,Revision:>=0};
}
