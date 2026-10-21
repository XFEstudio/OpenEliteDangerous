using System.Text;
using System.Text.RegularExpressions;

internal sealed record EndpointLiteral(string Rva,string Encoding,string Endpoint);
internal sealed record ApiPathLiteral(string Rva,string Encoding,string Path);
internal static partial class EndpointScanner
{
    // Static program literals only. Do not export query strings, fragments, user information or arbitrary nearby text.
    [GeneratedRegex(@"(?i)(?:https?|wss?|ftp|udp|tcp)://[a-z0-9._~:/%\-\[\]@?=&+#]+",RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    [GeneratedRegex(@"/?[0-9]+\.[0-9]+/(?:elite|edserver|server)/[a-zA-Z0-9_/%.:-]+",RegexOptions.CultureInvariant)]
    private static partial Regex ApiPattern();
    internal static string? Redact(string candidate)
    {
        var end=candidate.IndexOfAny(['?','#']);if(end>=0)candidate=candidate[..end];
        if(!Uri.TryCreate(candidate,UriKind.Absolute,out var uri)||uri.Host.Length==0) return null;
        var authority=uri.Scheme+"://"+(uri.HostNameType==UriHostNameType.IPv6?"["+uri.IdnHost.Trim('[',']')+"]":uri.IdnHost)
            +(uri.IsDefaultPort?"":":"+uri.Port);
        return (authority+uri.AbsolutePath).Length<=512?authority+uri.AbsolutePath:null;
    }
    internal static List<EndpointLiteral> Scan(byte[] bytes,Func<int,uint?> fileOffsetToRva,List<ApiPathLiteral>? apiPaths=null)
    {
        var results=new List<EndpointLiteral>();
        for(var i=0;i<bytes.Length;)
        {
            if(bytes[i] is <32 or >126) {i++;continue;}
            var start=i;while(i<bytes.Length&&bytes[i] is >=32 and <=126)i++;
            if(i-start>=7&&i-start<=4096) Add(Encoding.ASCII.GetString(bytes,start,i-start),start,1,"ASCII");
        }
        for(var i=0;i+1<bytes.Length;)
        {
            if(bytes[i] is <32 or >126||bytes[i+1]!=0) {i++;continue;}
            var start=i;while(i+1<bytes.Length&&bytes[i] is >=32 and <=126&&bytes[i+1]==0)i+=2;
            if(i-start>=14&&i-start<=8192)Add(Encoding.Unicode.GetString(bytes,start,i-start),start,2,"UTF-16LE");
        }
        return results.Distinct().ToList();
        void Add(string text,int start,int stride,string encoding)
        {
            if(text.Contains("://",StringComparison.Ordinal))
                foreach(Match match in Pattern().Matches(text))
                    if(Redact(match.Value) is { } endpoint&&fileOffsetToRva(start+match.Index*stride) is { } rva)
                        results.Add(new("0x"+rva.ToString("X"),encoding,endpoint));
            if(apiPaths is not null&&text.Contains('/'))
                foreach(Match match in ApiPattern().Matches(text))
                    if(fileOffsetToRva(start+match.Index*stride) is { } rva)
                        apiPaths.Add(new("0x"+rva.ToString("X"),encoding,"/"+match.Value.TrimStart('/')));
        }
    }
}
