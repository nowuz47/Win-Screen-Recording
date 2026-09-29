using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Glide.App;

internal static class Program
{
    private static string executable = Environment.ProcessPath!;
    private static string root = "";
    private static readonly List<object> results = [];
    private static int failed;
    private static readonly List<Process> children = [];
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Process Child(string profile, string mode, string tag)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "child", profile, mode, Path.Combine(root, tag) }) start.ArgumentList.Add(arg);
        var child = Process.Start(start)!; children.Add(child); return child;
    }
    private static string Line(Process child)
        => child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult() ?? throw new Exception("Child exited without result: " + child.StandardError.ReadToEnd());
    private static void End(Process child)
    {
        if (!child.HasExited) { child.StandardInput.WriteLine("exit"); child.StandardInput.Flush(); }
        Require(child.WaitForExit(8000), "Child failed to shut down.");
        Require(child.ExitCode == 0, "Child exit " + child.ExitCode + ": " + child.StandardError.ReadToEnd());
    }
    private static void Check(string name, Action action)
    {
        var watch=Stopwatch.StartNew();
        try { action(); results.Add(new { name, passed=true, elapsedMs=watch.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
        catch(Exception ex) { failed++; results.Add(new { name, passed=false, error=ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args.Length == 4 && args[0] == "child") return RunChild(args[1],args[2],args[3]);
        if(args.Length!=1 || Directory.Exists(args[0])) return 2;
        root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        try
        {
            Check("concurrent processes have exactly one profile owner and redirect all secondaries", () =>
            {
                string profile=Path.Combine(root,"race-profile");
                var contenders=Enumerable.Range(0,8).Select(i=>Child(profile,"normal","race-"+i)).ToArray();
                var responses=contenders.Select(Line).ToArray();
                Require(responses.Count(x=>x=="PRIMARY")==1,"Owner count: "+string.Join(",",responses));
                Require(responses.Count(x=>x=="Activated")==7,"Redirection failed: "+string.Join(",",responses));
                int primary=Array.IndexOf(responses,"PRIMARY");
                foreach(var p in contenders)End(p);
                Require(File.ReadAllLines(Path.Combine(root,"race-"+primary)).Length==7,"Activation acknowledgement count differs.");
            });
            Check("different profile owners are independent", () =>
            {
                using var a=new SingleInstanceGate(Path.Combine(root,"a"));using var b=new SingleInstanceGate(Path.Combine(root,"b"));
                Require(a.TryAcquire()&&b.TryAcquire(),"Independent profile excluded.");
            });
            Check("case and trailing separator cannot bypass the same profile lease", () =>
            {
                var owner=Child(Path.Combine(root,"normalized"),"normal","normalized");Require(Line(owner)=="PRIMARY","Owner missing.");
                var second=Child(Path.Combine(root,"NORMALIZED")+Path.DirectorySeparatorChar,"normal","normalized-second");
                Require(Line(second)=="Activated","Equivalent profile started twice.");End(second);End(owner);
            });
            Check("duplicate launch waits for delayed UI readiness", () =>
            {
                string profile=Path.Combine(root,"startup");var owner=Child(profile,"delayed","startup");Require(Line(owner)=="PRIMARY","Owner missing.");
                var second=Child(profile,"normal","startup-second");Require(Line(second)=="Activated","Startup activation lost.");End(second);End(owner);
            });
            Check("closing owner rejects activation while retaining its lease", () =>
            {
                string profile=Path.Combine(root,"closing");var owner=Child(profile,"closing","closing");Require(Line(owner)=="PRIMARY","Owner missing.");
                using(var second=new SingleInstanceGate(profile))
                {
                    Require(!second.TryAcquire(),"Closing lease released early.");
                    Require(second.ActivateExistingAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()==ActivationResult.Closing,"Closing not reported.");
                    Require(!second.TryAcquire(),"Rejected activation stole ownership.");
                }
                End(owner);using var next=new SingleInstanceGate(profile);Require(next.TryAcquire(),"Clean exit retained ownership.");
            });
            Check("crash releases ownership even while another process retains the mutex handle", () =>
            {
                string profile=Path.Combine(root,"crash");var owner=Child(profile,"normal","crash");Require(Line(owner)=="PRIMARY","Owner missing.");
                using var next=new SingleInstanceGate(profile);Require(!next.TryAcquire(),"Lease not exclusive.");
                owner.Kill(entireProcessTree:true);Require(owner.WaitForExit(8000),"Disposable child did not terminate.");
                Require(next.TryAcquire(),"Abandoned mutex prevented recovery.");
                next.StartListening();next.SetActivationHandler(_=>Task.FromResult(true));
            });
            Check("invalid IPC command is rejected and a later valid launcher still works", () =>
            {
                string profile=Path.Combine(root,"protocol");var owner=Child(profile,"normal","protocol");Require(Line(owner)=="PRIMARY","Owner missing.");
                using(var gate=new SingleInstanceGate(profile))
                using(var pipe=new NamedPipeClientStream(".",gate.PipeName,PipeDirection.InOut,PipeOptions.CurrentUserOnly))
                {
                    pipe.Connect(3000);pipe.WriteByte(0);Require(pipe.ReadByte()==(byte)ActivationResult.Unavailable,"Invalid command activated UI.");
                }
                var second=Child(profile,"normal","protocol-second");Require(Line(second)=="Activated","Listener did not recover.");End(second);End(owner);
                Require(File.ReadAllLines(Path.Combine(root,"protocol")).Length==1,"Invalid command reached UI.");
            });
            Check("silent client deadline allows a later valid launcher", () =>
            {
                string profile=Path.Combine(root,"timeout");var owner=Child(profile,"normal","timeout");Require(Line(owner)=="PRIMARY","Owner missing.");
                using(var gate=new SingleInstanceGate(profile))
                using(var pipe=new NamedPipeClientStream(".",gate.PipeName,PipeDirection.InOut,PipeOptions.CurrentUserOnly|PipeOptions.Asynchronous))
                {
                    pipe.Connect(3000);
                    try { Require(pipe.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()==0,"Silent client received an acknowledgement."); }
                    catch(IOException) { /* Server may close the pipe with EOF or a broken-pipe error. */ }
                }
                var second=Child(profile,"normal","timeout-second");Require(Line(second)=="Activated","Timed-out connection blocked the listener.");End(second);End(owner);
            });
            Check("owner shutdown with a connected silent client does not hang", () =>
            {
                string profile=Path.Combine(root,"silent");var owner=Child(profile,"normal","silent");Require(Line(owner)=="PRIMARY","Owner missing.");
                using var gate=new SingleInstanceGate(profile);
                using var pipe=new NamedPipeClientStream(".",gate.PipeName,PipeDirection.InOut,PipeOptions.CurrentUserOnly);
                pipe.Connect(3000);End(owner);Require(gate.TryAcquire(),"Shutdown with connected silent client retained lease.");
            });
        }
        finally
        {
            foreach(var child in children)
            {
                if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit(8000);}
                child.Dispose();
            }
        }
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new { timestamp=DateTimeOffset.UtcNow,
            os=Environment.OSVersion.ToString(),architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            tests=results.Count,failed,results,scope="Real Windows child processes on the linked instance gate. Does not test WinUI foreground, other accounts, other sessions, elevation or full app lifecycle."},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
    private static int RunChild(string profile,string mode,string log)
    {
        using var gate=new SingleInstanceGate(profile);
        if(!gate.TryAcquire()) { Console.WriteLine(gate.ActivateExistingAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());return 0; }
        gate.StartListening();Console.WriteLine("PRIMARY");
        if(mode=="delayed")Thread.Sleep(750);
        gate.SetActivationHandler(_=>{File.AppendAllText(log,"activate\n");return Task.FromResult(mode!="closing");});
        Console.ReadLine();return 0;
    }
}
