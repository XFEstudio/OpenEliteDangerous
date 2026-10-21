using System.Text.Json;
using XFEEliteDangerous.Client;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class OfflineDiagnosticLogTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"xfe-diagnostics-test-"+Guid.NewGuid().ToString("N"));
    [Fact]public void OperationsRetainParentSourceAndNestedException()
    {
        using(var log=new OfflineDiagnosticLog(directory,"test","synthetic-run"))
        using(var outer=log.Begin("test","outer"))
        {
            using(var inner=log.Begin("test","inner"))
            {
                try{ThrowFailure();}
                catch(Exception error){log.Write("error","test","failure","Expected fixture failure.",exception:error);inner.Complete("failed");}
            }
            log.Write("info","test","resumed");
        }
        var rows=Rows();var outerBegin=rows[0];var innerBegin=rows[1];var failure=rows[2];
        Assert.Equal(outerBegin.GetProperty("operationId").GetString(),innerBegin.GetProperty("parentOperationId").GetString());
        Assert.Equal(outerBegin.GetProperty("traceId").GetString(),failure.GetProperty("traceId").GetString());
        Assert.Equal(innerBegin.GetProperty("operationId").GetString(),failure.GetProperty("operationId").GetString());
        Assert.Equal(outerBegin.GetProperty("operationId").GetString(),rows[4].GetProperty("operationId").GetString());
        Assert.EndsWith("OfflineDiagnosticLogTests.cs",failure.GetProperty("source").GetProperty("file").GetString());
        Assert.True(failure.GetProperty("source").GetProperty("line").GetInt32()>0);
        Assert.Equal(nameof(OperationsRetainParentSourceAndNestedException),failure.GetProperty("source").GetProperty("member").GetString());
        var detail=failure.GetProperty("exception").GetProperty("detail").GetString()!;
        Assert.Contains(nameof(ThrowFailure),detail);Assert.Contains("IOException",detail);Assert.Contains("inner storage failure",detail);
    }
    [Fact]public void SecretsAreRemovedFromFieldsMessagesAndExceptions()
    {
        const string secret="synthetic-credential-never-print";var hex=new string('A',64);
        var encoded=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string('z',200)));
        using(var log=new OfflineDiagnosticLog(directory,"test","synthetic-run"))
        {
            log.RegisterSecret(secret);
            log.Write("error","test","redaction",secret+" "+hex+" "+encoded,new Dictionary<string,string?>
                {["authToken"]="another-credential",["queryFields"]="machineToken,cmdrId",["safe"]="fixture"},
                new InvalidDataException(secret));
        }
        var text=File.ReadAllText(Path.Combine(directory,"test.0000.jsonl"));
        Assert.DoesNotContain(secret,text);Assert.DoesNotContain(hex,text);Assert.DoesNotContain("another-credential",text);
        Assert.DoesNotContain(encoded,text);
        var row=Rows()[0];Assert.Equal("machineToken,cmdrId",row.GetProperty("fields").GetProperty("queryFields").GetString());
        Assert.Equal("fixture",row.GetProperty("fields").GetProperty("safe").GetString());
    }
    [Fact]public async Task ConcurrentWritesRemainCompleteOrderedJsonLines()
    {
        using(var log=new OfflineDiagnosticLog(directory,"test","synthetic-run"))
        {
            await Task.WhenAll(Enumerable.Range(0,128).Select(index=>Task.Run(()=>log.Write("debug","test","parallel",index.ToString()))));
            Assert.Equal(0,log.WriteFailures);
        }
        var rows=Rows();Assert.Equal(128,rows.Length);
        Assert.Equal(Enumerable.Range(1,128).Select(value=>(long)value),rows.Select(row=>row.GetProperty("sequence").GetInt64()));
    }
    [Fact]public void RotationPreservesEveryRecordInItsOwnSegment()
    {
        using(var log=new OfflineDiagnosticLog(directory,"test","synthetic-run",1024))
            for(var index=0;index<40;index++)log.Write("debug","test","rotation",new string('x',250));
        Assert.True(Directory.GetFiles(directory,"test.*.jsonl").Length>1);Assert.Equal(40,Rows().Length);
    }
    private static void ThrowFailure()
    {
        try{throw new IOException("inner storage failure");}
        catch(Exception error){throw new InvalidDataException("outer failure",error);}
    }
    private JsonElement[] Rows()=>Directory.GetFiles(directory,"test.*.jsonl").Order().SelectMany(File.ReadLines)
        .Select(line=>{using var document=JsonDocument.Parse(line);return document.RootElement.Clone();}).ToArray();
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
