using System.Text.Json;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Client;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeSoloAutomationRequest(int Schema,string CommandId,string Action,DateTimeOffset ExpiresAtUtc,int Attempts);

[JsonSerializable(typeof(NativeSoloAutomationRequest))]
public partial class NativeSoloAutomationJson:JsonSerializerContext;

/// <summary>Bounded menu intent only. Native eligibility and callback ownership stay with the game adapter.</summary>
public sealed class NativeSoloAutomation
{
    private readonly HashSet<string> consumed=new(StringComparer.Ordinal);
    private NativeSoloAutomationRequest? active;
    private bool waitingForSolo,issued,acknowledged;
    public string CommandId=>active?.CommandId??"";
    public int AttemptsIssued{get;private set;}
    public bool IsActive(DateTimeOffset now)=>active is {} request&&now<request.ExpiresAtUtc;
    public bool Arm(ReadOnlySpan<byte> document,DateTimeOffset now)
    {
        if(document.Length is 0 or >2048)throw new InvalidDataException("Invalid native SOLO command size.");
        var request=JsonSerializer.Deserialize(document,NativeSoloAutomationJson.Default.NativeSoloAutomationRequest)
            ??throw new InvalidDataException("Missing native SOLO command.");
        if(request.Schema!=1||request.Action!="enter-solo"||!Guid.TryParseExact(request.CommandId,"N",out _)
            ||request.Attempts is <1 or >3||request.ExpiresAtUtc<=now||request.ExpiresAtUtc>now.AddMinutes(10))
            throw new InvalidDataException("Invalid or expired native SOLO command.");
        if(consumed.Contains(request.CommandId))return false;
        if(consumed.Count>=128)throw new InvalidDataException("Native SOLO command limit reached for this process.");
        consumed.Add(request.CommandId);active=request;AttemptsIssued=0;
        waitingForSolo=issued=acknowledged=false;return true;
    }
    public string? WantedAction(string current,string target,DateTimeOffset now)
    {
        if(!IsActive(now)||current!=target||issued||AttemptsIssued>=active!.Attempts)return null;
        if(current=="MainMenu"&&!waitingForSolo)return "ContinueMenu";
        // A command may be armed while the user is already in ContinueMenu.
        return current=="ContinueMenu"?"SetupPlaySolo":null;
    }
    public bool CanAcknowledgeDisconnect(string current,string target,DateTimeOffset now)
        =>IsActive(now)&&!acknowledged&&current=="FailedToConnect"&&target==current;
    public void AcknowledgedDisconnect()
    {
        acknowledged=true;
        if(active is {} request&&AttemptsIssued<request.Attempts){issued=false;waitingForSolo=false;}
    }
    public void Issued(string action)
    {
        if(action=="ContinueMenu")waitingForSolo=true;
        else if(action=="SetupPlaySolo")
        {AttemptsIssued++;issued=true;waitingForSolo=false;acknowledged=false;}
        else throw new ArgumentException("Unknown native SOLO action.",nameof(action));
    }
}
