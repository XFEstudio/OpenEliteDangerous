using System.Text.Json;
using XFEEliteDangerous.Client;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class NativeSoloAutomationTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-20T15:00:00Z");
    private static byte[] Request(string? id=null,int attempts=1,string action="enter-solo",DateTimeOffset? expires=null)
        =>JsonSerializer.SerializeToUtf8Bytes(new NativeSoloAutomationRequest(1,id??Guid.NewGuid().ToString("N"),action,expires??Now.AddMinutes(3),attempts),NativeSoloAutomationJson.Default.NativeSoloAutomationRequest);
    [Fact]public void RepeatedCommandsCannotReplayMenuActions()
    {
        var intent=new NativeSoloAutomation();var command=Request();Assert.True(intent.Arm(command,Now));
        Assert.Equal("ContinueMenu",intent.WantedAction("MainMenu","MainMenu",Now));intent.Issued("ContinueMenu");
        Assert.Null(intent.WantedAction("MainMenu","MainMenu",Now));
        Assert.Equal("SetupPlaySolo",intent.WantedAction("ContinueMenu","ContinueMenu",Now));intent.Issued("SetupPlaySolo");
        Assert.False(intent.Arm(command,Now));Assert.Null(intent.WantedAction("ContinueMenu","ContinueMenu",Now));
        Assert.Equal(1,intent.AttemptsIssued);
    }
    [Fact]public void RetryRequiresRealFailureAcknowledgementAndHonorsAttemptLimit()
    {
        var intent=new NativeSoloAutomation();intent.Arm(Request(attempts:2),Now);
        for(var attempt=0;attempt<2;attempt++)
        {
            Assert.Equal("SetupPlaySolo",intent.WantedAction("ContinueMenu","ContinueMenu",Now));intent.Issued("SetupPlaySolo");
            Assert.Null(intent.WantedAction("MainMenu","MainMenu",Now));
            Assert.True(intent.CanAcknowledgeDisconnect("FailedToConnect","FailedToConnect",Now));intent.AcknowledgedDisconnect();
            Assert.False(intent.CanAcknowledgeDisconnect("FailedToConnect","FailedToConnect",Now));
        }
        Assert.Null(intent.WantedAction("MainMenu","MainMenu",Now));Assert.Equal(2,intent.AttemptsIssued);
    }
    [Fact]public void NoActionRunsDuringTransitionOrAfterDeadline()
    {
        var intent=new NativeSoloAutomation();intent.Arm(Request(),Now);
        Assert.Null(intent.WantedAction("MainMenu","ContinueMenu",Now));
        Assert.Null(intent.WantedAction("InGame","InGame",Now));
        Assert.Null(intent.WantedAction("MainMenu","MainMenu",Now.AddMinutes(4)));
        Assert.False(intent.CanAcknowledgeDisconnect("FailedToConnect","FailedToConnect",Now.AddMinutes(4)));
    }
    [Theory][InlineData(0)][InlineData(4)][InlineData(-1)]
    public void RejectsUnboundedAttempts(int attempts)=>Assert.Throws<InvalidDataException>(()=>new NativeSoloAutomation().Arm(Request(attempts:attempts),Now));
    [Theory][InlineData("enter-training")][InlineData("force-ready")][InlineData("exit-game")]
    public void RejectsUnrelatedActions(string action)=>Assert.Throws<InvalidDataException>(()=>new NativeSoloAutomation().Arm(Request(action:action),Now));
    [Fact]public void RejectsExpiredFarFutureMalformedAndUnknownFields()
    {
        var intent=new NativeSoloAutomation();
        Assert.Throws<InvalidDataException>(()=>intent.Arm(Request(expires:Now),Now));
        Assert.Throws<InvalidDataException>(()=>intent.Arm(Request(expires:Now.AddHours(1)),Now));
        Assert.Throws<InvalidDataException>(()=>intent.Arm(Request(id:"not-a-command-id"),Now));
        Assert.Throws<JsonException>(()=>intent.Arm("{\"Schema\":1,\"Unknown\":true}"u8,Now));
        Assert.Throws<InvalidDataException>(()=>intent.Arm(new byte[2049],Now));
    }
}
