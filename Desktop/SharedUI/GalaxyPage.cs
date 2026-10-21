using XFEEliteDangerous.Client;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XFEEliteDangerous.Desktop;

public sealed record GalaxyDocumentChoice(string Kind,string Key,string Label,string Payload);
public sealed class GalaxyPage : DesktopPage
{
    private readonly ComboBox documents=new(){MinWidth=460,DisplayMemberPath=nameof(GalaxyDocumentChoice.Label)};
    private readonly TextBox editor=new(){AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,MinHeight=360,MaxHeight=600,MaxLength=2*1024*1024};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap};
    private string version="";
    public GalaxyPage():base(AppText.Get("AppShellPage.Text100"),AppText.Get("GalaxyPage.Text173"))
    {
        if(!DesktopContext.IsAdmin){Body.Children.Add(new TextBlock{Text=AppText.Get("GalaxyPage.Text174")});return;}
        documents.Name="SharedGalaxyDocuments";editor.Name="SharedGalaxyEditor";
        documents.SelectionChanged+=(_,_)=>{if(documents.SelectedItem is GalaxyDocumentChoice value)editor.Text=value.Payload;};
        Body.Children.Add(Horizontal(documents,Button(AppText.Get("GalaxyPage.Text175"),()=>RunAsync(LoadAsync))));Body.Children.Add(status);
        var advanced=new Expander{Header=AppText.Get("GalaxyPage.Text176"),IsExpanded=true,Content=editor,HorizontalAlignment=HorizontalAlignment.Stretch};Body.Children.Add(advanced);
        var save=Button(AppText.Get("GalaxyPage.Text177"),()=>RunAsync(async()=>
        {
            if(documents.SelectedItem is not GalaxyDocumentChoice selected)throw new InvalidOperationException(AppText.Get("GalaxyPage.Text178"));
            await DesktopContext.Local.EditGalaxyAsync(version,selected.Kind,selected.Key,editor.Text,Lifetime.Token);await LoadAsync();Success(AppText.Get("GalaxyPage.Text179"));
        }),true);save.Name="SaveSharedGalaxy";Body.Children.Add(save);
        Body.Children.Add(new InfoBar{IsOpen=true,IsClosable=false,Severity=InfoBarSeverity.Informational,Title=AppText.Get("GalaxyPage.Text180"),Message=AppText.Get("GalaxyPage.Text181")});
        Loaded+=async(_,_)=>await RunAsync(LoadAsync);
    }
    private async Task LoadAsync()
    {
        var previous=(documents.SelectedItem as GalaxyDocumentChoice)?.Key;var data=await DesktopContext.Local.GalaxyAsync(Lifetime.Token);version=data.Text("version");
        var rows=data["definitions"].Rows().Select(value=>new GalaxyDocumentChoice("definition",value.Text("operation"),DocumentLabel(value.Text("operation")),value.Text("payload")))
            .Concat(data["systems"].Rows().Select(value=>new GalaxyDocumentChoice("system",value["address"]!.ToJsonString(),AppText.Get("GalaxyPage.Text182")+value["address"]!.ToJsonString(),value.Text("payload")))).ToArray();
        documents.ItemsSource=rows;documents.SelectedItem=rows.FirstOrDefault(value=>value.Key==previous)??rows.FirstOrDefault();
        status.Text=AppText.Format("GalaxyPage.Text183", (version), (data["systems"].Rows().Count()), (data["definitions"].Rows().Count()));
    }
    private static string DocumentLabel(string operation)=>operation switch
    {
        "/2.0/elite/resources/commodities"=>AppText.Get("GalaxyPage.Text184"),"/2.0/elite/powerplay2/powers/list"=>AppText.Get("GalaxyPage.Text185"),
        "/2.0/elite/resources/microresources"=>AppText.Get("GalaxyPage.Text186"),"/2.0/elite/resources/finance"=>AppText.Get("GalaxyPage.Text187"),
        "/2.0/elite/resources/permits"=>AppText.Get("GalaxyPage.Text188"),"/2.0/elite/resources/dictionary"=>AppText.Get("GalaxyPage.Text189"),
        "/2.0/elite/resources/loadingscreentips"=>AppText.Get("GalaxyPage.Text190"),"/2.0/elite/resources/ussregion/list"=>AppText.Get("GalaxyPage.Text191"),
        "/2.0/elite/synthesis/recipes"=>AppText.Get("GalaxyPage.Text192"),"/2.0/elite/colonisation/resources/optionsfulllist"=>AppText.Get("GalaxyPage.Text193"),
        "/2.0/elite/colonisation/resources/marketlink/weighting"=>AppText.Get("GalaxyPage.Text194"),_=>operation
    };
}
