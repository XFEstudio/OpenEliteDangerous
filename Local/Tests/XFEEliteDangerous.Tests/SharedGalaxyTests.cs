using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class SharedGalaxyTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"xfe-galaxy-"+Guid.NewGuid().ToString("N"));
    private const string Finance="/2.0/elite/resources/finance";
    private static NativeSystemMetadata System(ulong address=42)=>new(){Address=address,Id=1,Name="Fixture",LastChanged=0,CacheUntil=0,Population=0,Wealth=0,Security=0,StandardOfLiving=0,DevelopmentLevel=0,Distances=new(),Polities=new(),UnownedSettlements=new()};
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    [Fact]public void DefinitionsPersistAcrossInstancesAndLegacyCommanderWritesCannotEraseThem()
    {
        var first=new SharedGalaxyStore(root);first.Update("0","definition",Finance,"{\"privateRule\":7}");
        var second=new SharedGalaxyStore(root);Assert.Equal("{\"privateRule\":7}",second.Definition(Finance)!.Payload);
        File.WriteAllText(Path.Combine(root,"state.json"),"{}");Assert.Equal(first.Definition(Finance),new SharedGalaxyStore(root).Definition(Finance));
        Assert.Null(first.Definition("/2.0/elite/commander/savegame"));
    }
    [Fact]public void TheFirstPublicSystemSeedWinsForEveryAccountAndNewVersionsAreBackedUp()
    {
        var first=new SharedGalaxyStore(root);var original=first.System(42,System()).Payload;
        var other=new SharedGalaxyStore(root);Assert.Equal(original,other.System(42,System() with{Name="Other account metadata"}).Payload);
        var edit=JsonNode.Parse(original)!;edit["starsystem"]!["starsystem"]!["population"]=123;
        other.Update("1","system","42",edit.ToJsonString());Assert.Equal(123,JsonNode.Parse(first.System(42,null).Payload)!["starsystem"]!["starsystem"]!["population"]!.GetValue<int>());
        Assert.Single(Directory.GetFiles(Path.Combine(root,"galaxy-backups")));Assert.Equal(404,first.System(99,null).Status);
    }
    [Fact]public void StaleAndMalformedEditsAreAtomicAndDoNotChangeThePreviousRevision()
    {
        var store=new SharedGalaxyStore(root);store.Update("0","definition",Finance,"{}");var before=File.ReadAllBytes(Path.Combine(root,"galaxy.json"));
        Assert.Throws<SaveConflictException>(()=>store.Update("0","definition",Finance,"{\"x\":1}"));
        Assert.Throws<InvalidDataException>(()=>store.Update("1","system","42","{}"));
        Assert.Equal(before,File.ReadAllBytes(Path.Combine(root,"galaxy.json")));Assert.Equal(1,store.Snapshot().Revision);
    }
    [Theory][InlineData("/2.0/elite/user","{}")] [InlineData("/2.0/elite/commander/savegame","{}")] [InlineData("/2.0/elite/powerplay2/powers/list","{\"powers\":[]}")]
    public void PlayerDocumentsAndAnEmptyPowerCatalogueCannotBePublishedAsGlobalData(string operation,string content)
    {
        var store=new SharedGalaxyStore(root);Assert.Throws<ArgumentException>(()=>store.Update("0","definition",operation,content));Assert.Equal(0,store.Snapshot().Revision);
    }
    [Fact]public void ExternalXmlEntitiesAreRejectedBeforeAnyStateIsWritten()
    {
        var store=new SharedGalaxyStore(root);Assert.Throws<InvalidDataException>(()=>store.Update("0","definition","/2.0/elite/synthesis/recipes","<!DOCTYPE a [<!ENTITY data SYSTEM 'file:///unread'>]><a>&data;</a>"));
        Assert.False(File.Exists(Path.Combine(root,"galaxy.json")));
    }
}
