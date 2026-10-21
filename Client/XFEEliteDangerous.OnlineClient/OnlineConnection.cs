using XFEEliteDangerous.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.OnlineClient;

public sealed class GameServerException(int status,string message):IOException(message){public int Status{get;}=status;}

/// <summary>The sole game network boundary. Endpoint and exact TLS certificate are explicit;
/// no proxy, redirect, official-service fallback or global certificate trust changes.</summary>
public sealed class OnlineConnection : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly Uri address;
    private string? token;
    public OnlineServerProfile Server{get;}
    public OnlineGameLease? Lease{get;private set;}
    public OnlineConnection(OnlineServerProfile server)
    {
        if(!Uri.TryCreate(server.Address,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.AbsolutePath!="/game/"||!string.IsNullOrEmpty(uri.UserInfo+uri.Query+uri.Fragment)
            ||server.CertificateSha256.Length!=64||!server.CertificateSha256.All(Uri.IsHexDigit))throw new ArgumentException(AppText.Get("OnlineConnection.Text043"));
        address=uri;Server=server;var pin=Convert.FromHexString(server.CertificateSha256);
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,MaxConnectionsPerServer=4};
        handler.ServerCertificateCustomValidationCallback=(_,certificate,_,_)=>certificate is not null&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData),pin);
        client=new(handler){Timeout=TimeSpan.FromSeconds(25)};
    }
    public async Task<OnlineGameLease> LoginAsync(string email,string password,CancellationToken cancel=default)
        =>await ConnectAsync(new JsonObject{["email"]=email,["password"]=password},null,cancel);
    public async Task<OnlineGameLease> OpenLocalAsync(string account,string key,CancellationToken cancel=default)
    {
        if(!IPAddress.TryParse(address.Host,out var ip)?address.Host!="localhost":!IPAddress.IsLoopback(ip))throw new UnauthorizedAccessException(AppText.Get("OnlineConnection.Text044"));
        return await ConnectAsync(new JsonObject{["accountId"]=account},key,cancel);
    }
    private async Task<OnlineGameLease> ConnectAsync(JsonObject request,string? key,CancellationToken cancel)
    {
        if(Lease is not null)throw new InvalidOperationException(AppText.Get("OnlineConnection.Text045"));
        var result=await PostAsync("connect",request.ToJsonString(),key,cancel);
        Lease=JsonSerializer.Deserialize(result,GameLinkJson.Default.OnlineGameLease)??throw new InvalidDataException(AppText.Get("OnlineConnection.Text046"));
        token=Lease.Token;return Lease;
    }
    public void Resume(OnlineGameLease lease){if(Lease is not null||lease.Token.Length!=64||!lease.Token.All(Uri.IsHexDigit))throw new InvalidDataException(AppText.Get("OnlineConnection.Text047"));Lease=lease;token=lease.Token;}
    public OnlineGameLease DetachLease(){var lease=Lease??throw new InvalidOperationException(AppText.Get("OnlineConnection.Text048"));Lease=null;token=null;return lease;}
    public async Task<GameLinkReply> InvokeAsync(GameLinkRequest request,CancellationToken cancel=default)
    {
        if(token is null)throw new InvalidOperationException(AppText.Get("OnlineConnection.Text049"));
        var json=await PostAsync("invoke",JsonSerializer.Serialize(request,GameLinkJson.Default.GameLinkRequest),token,cancel);
        return JsonSerializer.Deserialize(json,GameLinkJson.Default.GameLinkReply)??throw new InvalidDataException(AppText.Get("OnlineConnection.Text050"));
    }
    private async Task<string> PostAsync(string path,string json,string? bearer,CancellationToken cancel)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(address,path));
        request.Headers.AcceptLanguage.ParseAdd(AppText.Language);
        request.Content=new StringContent(json,Encoding.UTF8,"application/json");if(bearer is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel).ConfigureAwait(false);
        if(response.Content.Headers.ContentLength is >24*1024*1024)throw new InvalidDataException(AppText.Get("OnlineConnection.Text051"));
        await using var stream=await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);using var bytes=new MemoryStream();var buffer=new byte[16384];
        int count;while((count=await stream.ReadAsync(buffer,cancel).ConfigureAwait(false))!=0){if(bytes.Length+count>24*1024*1024)throw new InvalidDataException(AppText.Get("OnlineConnection.Text051"));bytes.Write(buffer,0,count);}
        var text=Encoding.UTF8.GetString(bytes.GetBuffer(),0,(int)bytes.Length);
        if(!response.IsSuccessStatusCode)
        {
            string? detail=null;try{detail=JsonNode.Parse(text)?["error"]?.GetValue<string>();}catch(JsonException){}
            throw new GameServerException((int)response.StatusCode,detail??AppText.Format("OnlineConnection.Text052", ((int)response.StatusCode)));
        }
        return text;
    }
    public async ValueTask DisposeAsync()
    {
        if(token is not null)
        {
            using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(2));try{await PostAsync("disconnect","{}",token,cancel.Token).ConfigureAwait(false);}catch(Exception error)when(error is HttpRequestException or IOException or OperationCanceledException){}
            token=null;Lease=null;
        }
        client.Dispose();
    }
}
