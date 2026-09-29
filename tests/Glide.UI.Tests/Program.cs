using System.Text.Json;
using Glide.App;

var folder=Path.Combine(Path.GetTempPath(),"Glide-ui-preferences-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int failed=0;List<object> results=[];
void Check(string name,Action test)
{
    try{test();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}
    catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.Message});Console.WriteLine("FAIL "+name+": "+ex.Message);}
}
void Require(bool value){if(!value)throw new Exception("Assertion failed");}
try
{
    Check("legacy theme loads with standard UI motion",()=>{var p=UiPreferences.Parse("{\"theme\":1}");Require(p.Theme==1&&p.Motion==UiMotion.Standard);});
    Check("saving either preference preserves the other and unknown settings",()=>
    {
        var p=UiPreferences.Parse("{\"theme\":1,\"uiMotion\":1,\"future\":{\"value\":true}}");p.Theme=2;
        var path=Path.Combine(folder,"prefs.json");p.Save(path);var read=UiPreferences.Load(path);
        Require(read.Theme==2&&read.Motion==UiMotion.Reduced);
        read.Motion=UiMotion.Standard;read.Save(path);read=UiPreferences.Load(path);Require(read.Theme==2&&read.Motion==UiMotion.Standard);
        using var doc=JsonDocument.Parse(File.ReadAllText(path));Require(doc.RootElement.GetProperty("future").GetProperty("value").GetBoolean());
    });
    Check("invalid optional settings default without losing valid theme",()=>{var p=UiPreferences.Parse("{\"theme\":1,\"uiMotion\":\"bad\"}");Require(p.Theme==1&&p.Motion==UiMotion.Standard);});
    Check("Windows animation disable always reduces UI motion",()=>
    {
        Require(UiPreferences.ReducesMotion(UiMotion.Standard,false));Require(UiPreferences.ReducesMotion(UiMotion.Reduced,true));Require(!UiPreferences.ReducesMotion(UiMotion.Standard,true));
    });
    Check("unreadable JSON is reported without replacing the file",()=>
    {
        var path=Path.Combine(folder,"bad.json");File.WriteAllText(path,"{bad");bool rejected=false;
        try{UiPreferences.Load(path);}catch(JsonException){rejected=true;}Require(rejected&&File.ReadAllText(path)=="{bad");
    });
    Check("failed settings write preserves the previous valid destination",()=>
    {
        var path=Path.Combine(folder,"blocked");Directory.CreateDirectory(path);bool rejected=false;
        try{UiPreferences.Parse("{\"theme\":2}").Save(path);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){rejected=true;}
        Require(rejected&&Directory.Exists(path));
    });
}
finally{Directory.Delete(folder,true);}
Directory.CreateDirectory(".artifacts");
File.WriteAllText(".artifacts/ui-settings-tests.json",JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,tests=results.Count,failed,results},new JsonSerializerOptions{WriteIndented=true}));
return failed==0?0:1;
