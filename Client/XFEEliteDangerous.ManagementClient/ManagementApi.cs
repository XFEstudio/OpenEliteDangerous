using XFEEliteDangerous.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.ManagementClient;

/// <summary>The local management transport; native game data has no dependency on it.</summary>
public sealed class ManagementApi : IDisposable
{
    private readonly HttpClient client=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(30)};
    public Uri Address {get;}
    public ManagementApi(string address)
    {
        Address=new Uri(address.TrimEnd('/')+"/",UriKind.Absolute);
        if(Address.Scheme!="http"||!IPAddress.TryParse(Address.Host,out var ip)||!IPAddress.IsLoopback(ip)
            ||Address.UserInfo!=""||Address.Query!=""||Address.Fragment!="")
            throw new ArgumentException(AppText.Get("ManagementApi.Text038"));
    }
    public async Task<JsonNode> CallAsync(string operation,JsonObject? arguments=null,string? token=null,CancellationToken cancel=default)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(Address,"control"));
        request.Headers.AcceptLanguage.ParseAdd(AppText.Language);
        request.Content=new StringContent(new JsonObject{["operation"]=operation,["arguments"]=arguments?.DeepClone()??new JsonObject()}.ToJsonString(),Encoding.UTF8,"application/json");
        if(!string.IsNullOrEmpty(token))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel).ConfigureAwait(false);
        if(response.Content.Headers.ContentLength>8*1024*1024)throw new InvalidDataException(AppText.Get("ManagementApi.Text039"));
        await using var stream=await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var bytes=new MemoryStream();var buffer=new byte[16384];int read;
        while((read=await stream.ReadAsync(buffer,cancel).ConfigureAwait(false))>0)
        {if(bytes.Length+read>8*1024*1024)throw new InvalidDataException(AppText.Get("ManagementApi.Text039"));bytes.Write(buffer,0,read);}
        var result=JsonNode.Parse(bytes.GetBuffer().AsSpan(0,(int)bytes.Length))??throw new InvalidDataException(AppText.Get("ManagementApi.Text040"));
        var message=(result as JsonObject)?["error"]?.ToString()??AppText.Get("ManagementApi.Text041");
        if(response.StatusCode==HttpStatusCode.Unauthorized)throw new LocalSessionException(message);
        if(response.StatusCode==HttpStatusCode.Forbidden)throw new UnauthorizedAccessException(message);
        if(response.StatusCode==HttpStatusCode.Conflict)
        {
            if ((result as JsonObject)?["code"]?.ToString() == "save_conflict" ||
                new[] { "en-US", "zh-CN" }.Any(language => message == AppText.Catalog(language)["NativeTutorialSelection.Text382"]))
                throw new SaveConflictException();
            throw new InvalidOperationException(message);
        }
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException(message);
        return result;
    }
    public void Dispose()=>client.Dispose();
}
