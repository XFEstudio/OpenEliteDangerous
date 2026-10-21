using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if(args is ["--self-test"]) return AuditSelfTest.Run();
if(args is ["--solo-static"])return await PeNetworkAudit.AuditSoloAsync();
if(args is not ["--scan-game"])
{
    Console.WriteLine("--scan-game: read-only static network inventory of E:\\Elite Dangerous; writes a new project artifacts report. --self-test: synthetic parser checks only.");
    return 78;
}
const string game=@"E:\Elite Dangerous";
const string supported="e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
var product=Path.Combine(game,"Products","elite-dangerous-odyssey-64");
var main=Path.Combine(product,"EliteDangerous64.exe");
using(var input=File.OpenRead(main))
    if(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!=supported)
        throw new InvalidDataException("The fixed game build differs. No image was loaded or modified.");
var root=new DirectoryInfo(AppContext.BaseDirectory);
while(root is not null&&!File.Exists(Path.Combine(root.FullName,"XFEEliteDangerous.slnx"))) root=root.Parent;
if(root is null) throw new DirectoryNotFoundException("Run the audit from its repository build.");
var output=Path.Combine(root.FullName,"artifacts","network-call-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
var reports=new List<ImageAudit>();var failures=new List<object>();
var paths=new[]{product,game}.SelectMany(path=>Directory.EnumerateFiles(path,"*",SearchOption.TopDirectoryOnly))
    .Where(path=>Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll").Order(StringComparer.OrdinalIgnoreCase).ToArray();
foreach(var path in paths)
{
    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("A game image is a reparse point.");
    try
    {
        var report=PeNetworkAudit.Read(path,Path.GetRelativePath(game,path),path==main);
        reports.Add(report);
        Console.WriteLine(JsonSerializer.Serialize(new{image=report.Image,networkImports=report.Imports.Count,
            nativeNetworkCallSites=report.NativeCalls.Count,managedNetworkCallSites=report.ManagedCalls.Count}));
    }
    catch(Exception error) when(error is BadImageFormatException or InvalidDataException)
    {failures.Add(new{image=Path.GetRelativePath(game,path),error=error.GetType().Name});}
}
var sites=reports.SelectMany(image=>image.NativeCalls.Select(call=>new{image=image.Image,kind="native",call.Api,call.Category,site=call.SiteRva,owner=call.FunctionRva})
    .Concat(image.ManagedCalls.Select(call=>new{image=image.Image,kind="managed-il",call.Api,call.Category,site=call.IlOffset,owner=call.Method}))).ToArray();
var summary=new
{
    schema="xfeelite.static-network-call-inventory.v1",createdAt=DateTimeOffset.UtcNow,gameRoot=game,executableSha256=supported,
    scope="Main product EXE/DLL files and top-level launcher/support EXE/DLL files. Binary static analysis; no original C++ source is present.",
    counts=new{images=reports.Count,nativeImages=reports.Count(image=>!image.Managed),managedImages=reports.Count(image=>image.Managed),
        networkImportEntries=reports.Sum(image=>image.Imports.Count),nativeNetworkCallSites=reports.Sum(image=>image.NativeCalls.Count),
        managedNetworkCallSites=reports.Sum(image=>image.ManagedCalls.Count),dynamicResolverCallSites=reports.Sum(image=>image.DynamicResolverCalls),
        endpointLiteralOccurrences=reports.Sum(image=>image.Endpoints.Count),apiPathLiterals=reports.Sum(image=>image.ApiPaths.Count),
        distinctApiPaths=reports.SelectMany(image=>image.ApiPaths).Select(path=>path.Path).Distinct().Count(),apiPathReferences=reports.Sum(image=>image.ApiPathReferences.Count),imageFailures=failures.Count},
    categories=sites.GroupBy(site=>site.Category).Select(group=>new{category=group.Key,callSites=group.Count(),apis=group.Select(site=>site.Api).Distinct().Count()}).OrderByDescending(group=>group.callSites),
    nativeEffects=reports.SelectMany(report=>report.NativeCalls).GroupBy(call=>call.Effect).Select(group=>new{effect=group.Key,callSites=group.Count()}),
    main=reports.Single(report=>report.IsMain),images=reports,failures,
    limitations=new[]{"Imports and static call sites are not runtime request counts or proof that a feature requires a connection.",
        "Native calls are decoded inside x64 PE exception-table function ranges. Register-indirect calls, dynamically resolved APIs, packed/encrypted code and unindexed leaf functions are not completely resolved.",
        "Managed IL calls resolve metadata member references; reflection, delegates and external/runtime assemblies may add calls.",
        "Endpoint strings are candidates only. Query, fragment and user-info are omitted. No process memory, profile, authentication data or game logs are read.",
        "Known internal libcurl/UDP labels are separately identified pinned-build annotations. Direct callers are computed; virtual dispatch is not claimed resolved.",
        "No image is loaded, executed or patched, no network request is sent, and no running game is stopped or controlled."}
};
await File.WriteAllTextAsync(Path.Combine(output,"inventory.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
var csv=new StringBuilder("image,kind,category,api,site,owner\r\n");
foreach(var site in sites) csv.AppendLine(string.Join(',',new[]{site.image,site.kind,site.Category,site.Api,site.site,site.owner}.Select(value=>'"'+value.Replace("\"","\"\"")+'"')));
await File.WriteAllTextAsync(Path.Combine(output,"call-sites.csv"),csv.ToString(),new UTF8Encoding(true));
var routes=new StringBuilder("image,path,literal_rva,reference_rva,function_rva\r\n");
foreach(var image in reports)
    foreach(var path in image.ApiPaths)
    {
        var refs=image.ApiPathReferences.Where(reference=>reference.LiteralRva==path.Rva).ToArray();
        if(refs.Length==0)Row(image.Image,path.Path,path.Rva,"","");
        else foreach(var reference in refs)Row(image.Image,path.Path,path.Rva,reference.SiteRva,reference.FunctionRva);
    }
await File.WriteAllTextAsync(Path.Combine(output,"api-paths.csv"),routes.ToString(),new UTF8Encoding(true));
var contracts=reports.SelectMany(image=>image.ApiPaths.Select(path=>path.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
    .Select(path=>new{path,methods=reports.SelectMany(image=>image.ApiMethods).Where(method=>method.Path==path).Select(method=>method.Method).Distinct().Order(),
        methodEvidence=reports.SelectMany(image=>image.ApiMethods).Where(method=>method.Path==path).ToArray(),
        references=reports.SelectMany(image=>image.ApiPathReferences.Where(reference=>reference.Path==path).Select(reference=>new{image=image.Image,reference.LiteralRva,reference.SiteRva,reference.FunctionRva})).ToArray(),
        responseContract="pending-review"}).ToArray();
await File.WriteAllTextAsync(Path.Combine(output,"api-contracts.json"),JsonSerializer.Serialize(new{schema="xfeelite.native-api-contract-inventory.v1",executableSha256=supported,
    scope="All static paths; constant-argument request-constructor methods only. Response shapes and semantics require separate reader review.",endpoints=contracts},new JsonSerializerOptions{WriteIndented=true}));
var mainReport=reports.Single(report=>report.IsMain);
var note=new StringBuilder("# 游戏网络调用统计\n\n");
note.AppendLine("仅静态读取游戏程序和 DLL；不加载代码，不修改或操控正在运行的游戏。原始 C++ 源码不在本机，报告基于 PE、x64 指令、.NET IL 和字符串引用。\n");
note.AppendLine($"扫描 {reports.Count} 个文件；主程序网络相关导入 {mainReport.Imports.Count} 项、静态调用点 {mainReport.NativeCalls.Count} 处。发现主程序 API 路径 {mainReport.ApiPaths.Select(path=>path.Path).Distinct().Count()} 条、路径引用 {mainReport.ApiPathReferences.Count} 处。\n");
note.AppendLine("相关 API 调用点包含纯本地辅助函数，不能当作实际请求次数或联网功能数量。\n\n| 主程序调用类型 | 调用点 |\n| --- | ---: |");
foreach(var group in mainReport.NativeCalls.GroupBy(call=>call.Effect).OrderByDescending(group=>group.Count()))note.AppendLine($"| {group.Key} | {group.Count()} |");
note.AppendLine("\n文件：`inventory.json` 为完整清单和两级直接调用关系；`call-sites.csv` 为逐调用点；`api-paths.csv` 为路径、字符串 RVA、引用点和所属函数。动态函数查找、寄存器间接调用及外部模块仍可能增加调用，无法仅靠静态分析证明穷尽。\n");
await File.WriteAllTextAsync(Path.Combine(output,"README.md"),note.ToString());
Console.WriteLine(JsonSerializer.Serialize(new{report=output,summary.counts,methodsResolved=contracts.Count(contract=>contract.methods.Any())}));return 0;

void Row(params string[] values)=>routes.AppendLine(string.Join(',',values.Select(value=>'"'+value.Replace("\"","\"\"")+'"')));
