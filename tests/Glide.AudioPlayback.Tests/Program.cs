using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Glide.App;
using Glide.Core;
using Windows.Media.Playback;

if (!OperatingSystem.IsWindows() || args.Length != 2) return 2;
string resultRoot=Path.GetFullPath(args[1]);if(Directory.Exists(resultRoot))return 2;Directory.CreateDirectory(resultRoot);
string? fixture=null;
foreach(string directory in Directory.EnumerateDirectories(args[0]).OrderByDescending(Directory.GetLastWriteTimeUtc))
{
    string projects=Path.Combine(directory,"projects");if(!Directory.Exists(projects)||!File.Exists(Path.Combine(directory,"result.json")))continue;
    var candidate=new ProjectStore(projects);
    if(candidate.List().Any(p=>p.Name=="Controlled audio on actual video"&&p.Audio?.Length==2)){fixture=projects;break;}
}
if(fixture is null)throw new InvalidDataException("Run the controlled-audio Windows render tests first.");
var store=new ProjectStore(fixture);var project=store.List().Single(p=>p.Name=="Controlled audio on actual video"&&p.Audio?.Length==2);
var results=new List<object>();int failed=0;
void Check(bool value,string message){if(!value)throw new InvalidDataException(message);}
async Task Until(Func<bool> condition,Func<Exception?> error,int milliseconds=8000)
{
    var watch=Stopwatch.StartNew();
    while(!condition()){if(error() is Exception ex)throw ex;if(watch.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Playback condition timed out");await Task.Delay(20);}
    if(error() is Exception failure)throw failure;
}
async Task Run(string name,Func<Task<object>> test)
{
    var watch=Stopwatch.StartNew();
    try{var evidence=await test();results.Add(new{name,passed=true,milliseconds=watch.Elapsed.TotalMilliseconds,evidence});Console.WriteLine("PASS "+name);}
    catch(Exception e){failed++;results.Add(new{name,passed=false,milliseconds=watch.Elapsed.TotalMilliseconds,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}
}
await Run("actual audio endpoint enumeration preserves microphone and system roles",async()=>
{
    var devices=await Task.Run(NativeAudio.Devices);
    Check(devices.Any(d=>d.Role==2),"No render endpoint available for the playback fixture");
    Check(devices.All(d=>!string.IsNullOrEmpty(d.Id)&&!string.IsNullOrEmpty(d.Name)&&d.Role is 1 or 2),"Invalid endpoint metadata");
    Check(devices.Select(d=>(d.Role,d.Id)).Distinct().Count()==devices.Length,"Duplicate endpoints");
    return new{microphoneEndpoints=devices.Count(d=>d.Role==1),systemEndpoints=devices.Count(d=>d.Role==2),capturedMicrophone=false};
});
await Run("actual MediaPlayer consumes edited PCM through end of stream",async()=>
{
    using var source=new AudioPreviewSource(store,project,0);using var player=new MediaPlayer{AutoPlay=false,Volume=0};
    Exception? failure=null;bool ended=false;player.MediaFailed+=(_,e)=>failure=e.ExtendedErrorCode;player.MediaEnded+=(_,_)=>Volatile.Write(ref ended,true);
    player.Source=source.Source;player.Play();
    await Until(()=>player.PlaybackSession.Position.TotalSeconds>.3,()=>failure??source.Failure);
    double running=player.PlaybackSession.Position.TotalSeconds;
    await Until(()=>Volatile.Read(ref ended),()=>failure??source.Failure);
    Check(AudioStore.Load(store,project.Id).Issues.Length==0,"Preview changed audio originals");
    return new{runningSeconds=running,ended,mutedDeviceOutput=true};
});
await Run("nonzero preview start selects the requested audio timeline position",async()=>
{
    using var source=new AudioPreviewSource(store,project,1_200_000);using var player=new MediaPlayer{AutoPlay=false,Volume=0};
    Exception? failure=null;player.MediaFailed+=(_,e)=>failure=e.ExtendedErrorCode;player.Source=source.Source;player.Play();
    await Until(()=>player.PlaybackSession.Position.TotalSeconds>=1.3,()=>failure??source.Failure);
    double position=player.PlaybackSession.Position.TotalSeconds;Check(position<2.0,"Nonzero start jumped to an unexpected position");
    return new{requestedSeconds=1.2,observedSeconds=position};
});
await Run("pause holds the audio clock and resume advances it",async()=>
{
    using var source=new AudioPreviewSource(store,project,0);using var player=new MediaPlayer{AutoPlay=false,Volume=0};
    Exception? failure=null;player.MediaFailed+=(_,e)=>failure=e.ExtendedErrorCode;player.Source=source.Source;player.Play();
    await Until(()=>player.PlaybackSession.Position.TotalSeconds>.4,()=>failure??source.Failure);player.Pause();
    await Until(()=>player.PlaybackSession.PlaybackState==MediaPlaybackState.Paused,()=>failure??source.Failure);
    double before=player.PlaybackSession.Position.TotalSeconds;await Task.Delay(250);double after=player.PlaybackSession.Position.TotalSeconds;
    Check(Math.Abs(after-before)<=.02,"Paused audio clock advanced");player.Play();await Until(()=>player.PlaybackSession.Position.TotalSeconds>after+.2,()=>failure??source.Failure);
    return new{pauseDeltaSeconds=after-before,resumed=true};
});
await Run("repeated in-flight stop and source disposal release verified file leases",async()=>
{
    for(int i=0;i<8;i++)
    {
        var source=new AudioPreviewSource(store,project,i%2==0?0:1_000_000);using var player=new MediaPlayer{AutoPlay=false,Volume=0};
        Exception? failure=null;player.MediaFailed+=(_,e)=>failure=e.ExtendedErrorCode;player.Source=source.Source;player.Play();
        try{await Until(()=>player.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,()=>failure??source.Failure);await Task.Delay(30);}
        finally{player.Pause();player.Source=null;source.Dispose();source.Dispose();}
    }
    string path=Path.Combine(store.ProjectPath(project.Id),"audio","microphone-000000.wav");
    using var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
    return new{iterations=8,exclusiveReadAfterDispose=true};
});
string Digest(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
File.WriteAllText(Path.Combine(resultRoot,"result.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,
    os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),
    scope="Actual Windows MediaPlayer/MSS on the application's linked AudioPreviewSource; controlled PCM and muted device output. No physical speaker/microphone or video-latency certification.",
    fixture,projectId=project.Id,assemblySha256=Digest(typeof(AudioPreviewSource).Assembly.Location),coreSha256=Digest(typeof(ProjectStore).Assembly.Location),nativeSha256=Digest(Path.Combine(AppContext.BaseDirectory,"Glide.Capture.dll")),tests=results.Count,failed,results},new JsonSerializerOptions{WriteIndented=true}));
return failed==0?0:1;
