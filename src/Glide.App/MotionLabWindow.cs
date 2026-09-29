using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glide.App;

// Opt-in development surface. Exercises the real compositor without recording the desktop or changing user settings.
internal sealed class MotionLabWindow : Window
{
    private readonly MotionPolicy policy;
    private readonly MotionCoordinator motion;
    private readonly CancellationTokenSource closed = new();
    private readonly Border sample;
    private readonly Grid ringHost = new() { Width=88, Height=88 };
    private readonly TextBlock status = new() { Text="카드를 눌러 모션을 확인하세요.", TextWrapping=TextWrapping.Wrap };
    public MotionLabWindow(bool automated)
    {
        Title="Glide · UI 모션 확인"; AppWindow.Resize(new(760,520));
        policy=new(DispatcherQueue,UiMotion.Standard);motion=new(policy);
        var body=new StackPanel {Padding=new Thickness(32),Spacing=20,RequestedTheme=ElementTheme.Dark};
        body.Background=(Brush)Application.Current.Resources["GlideBackground"];
        body.Children.Add(new TextBlock {Text="유려한 제품 모션",FontSize=26});body.Children.Add(status);
        var card=new Button {Content="홈 → 준비 화면",HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(24)};
        sample=new Border {Padding=new Thickness(24),CornerRadius=new CornerRadius(12),Background=(Brush)Application.Current.Resources["GlideSurface"],Child=new TextBlock {Text="미리보기와 제어가 자연스럽게 이어집니다.",TextWrapping=TextWrapping.Wrap}};
        body.Children.Add(card);body.Children.Add(sample);body.Children.Add(ringHost);
        motion.AttachButton(card,true);card.Click+=(_,_)=>motion.Enter(sample,MotionTokens.Page,x:16,y:0);
        var controls=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};
        var reduced=new ToggleSwitch {Header="모션 감소"};reduced.Toggled+=(_,_)=>policy.SetPreference(reduced.IsOn?UiMotion.Reduced:UiMotion.Standard);
        var complete=new Button {Content="완료 피드백"};complete.Click+=(_,_)=>motion.Feedback(sample);
        controls.Children.Add(reduced);controls.Children.Add(complete);body.Children.Add(controls);Content=body;
        Closed+=(_,_)=>{closed.Cancel();motion.Dispose();policy.Dispose();};
        if(automated)body.Loaded+=async(_,_)=>await Verify();
    }
    private async Task Verify()
    {
        List<object> results=[];
        void Trace(string message)
        {
            var path=Environment.GetEnvironmentVariable("GLIDE_MOTION_EVIDENCE");
            if(path is null)return;
            Directory.CreateDirectory(path);File.AppendAllText(Path.Combine(path,"progress.log"),$"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        async Task Check(string name,Func<Task> test)
        {
            Trace("START "+name);
            try{await test();results.Add(new{name,passed=true});Trace("PASS "+name);}
            catch(Exception ex){results.Add(new{name,passed=false,error=ex.ToString()});Trace("FAIL "+name+" "+ex);}
        }
        Task Wait(int ms)=>Task.Delay(ms,closed.Token);
        static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        try
        {
            await Wait(100);
            await Check("real WinUI composition runs and settles",async()=>
            {
                Require(sample.XamlRoot is not null && sample.ActualWidth>0,"No live XAML surface");
                motion.Enter(sample,MotionTokens.Preview,x:16,y:0);
                Require(motion.ActiveCount>0,"Animation was not submitted");
                await Wait(500);
                Require(motion.ActiveCount==0&&sample.Opacity==1&&sample.Translation==Vector3.Zero&&sample.Scale==Vector3.One,"Animation did not settle");
                Require(motion.FallbackCount==0,"Composition API fallback occurred");
            });
            await Check("interrupted exits cannot collapse a re-entered element",async()=>
            {
                int obsoleteCompletions=0;
                for(int i=0;i<100;i++)
                {
                    motion.Exit(sample,()=>obsoleteCompletions++);
                    motion.Enter(sample,MotionTokens.Preview,x:16,y:0);
                }
                await Wait(500);
                Require(obsoleteCompletions==0&&motion.ActiveCount==0,"Stale completion or remaining animation");
            });
            await Check("motion reduction cancels movement and preserves terminal state",async()=>
            {
                motion.Enter(sample,MotionTokens.Page,y:16,scale:.92f);policy.SetPreference(UiMotion.Reduced);
                Require(motion.ActiveCount==0&&sample.Scale==Vector3.One&&sample.Translation==Vector3.Zero,"Preference change left motion active");
                bool finished=false;motion.Exit(sample,()=>finished=true);await Wait(300);
                Require(finished&&motion.ActiveCount==0,"Reduced fade did not complete");
                policy.SetPreference(UiMotion.Standard);
            });
            await Check("hidden windows stop work and finalize exits",async()=>
            {
                bool collapsed=false;motion.Exit(sample,()=>collapsed=true);motion.SetVisible(false);
                motion.Enter(sample);
                Require(collapsed&&motion.ActiveCount==0,"Hidden window retained decorative work");
                motion.SetVisible(true);await Wait(20);
            });
            await Check("removed controls release handlers and animations",async()=>
            {
                int before=motion.AttachedButtonCount;
                for(int i=0;i<100;i++){var button=new Button();motion.AttachButton(button);motion.DetachButton(button);}
                Require(motion.AttachedButtonCount==before,"Button registrations accumulated");await Wait(20);
            });
            await Check("countdown ring survives reduction and disposal",async()=>
            {
                using(var ring=new CountdownRing(ringHost,policy,Windows.UI.Color.FromArgb(255,80,205,175)))
                {ring.Step(3);await Wait(100);policy.SetPreference(UiMotion.Reduced);ring.Step(2);await Wait(100);}
                policy.SetPreference(UiMotion.Standard);
            });
            Require(motion.FallbackCount==0,"Unexpected animation fallback");
        }
        catch(Exception ex){results.Add(new{name="harness",passed=false,error=ex.ToString()});}
        string? evidence=Environment.GetEnvironmentVariable("GLIDE_MOTION_EVIDENCE");
        if(evidence is not null)
        {
            Directory.CreateDirectory(evidence);
            string assembly=typeof(MotionLabWindow).Assembly.Location;
            var report=new {timestamp=DateTimeOffset.UtcNow,os=Environment.OSVersion.ToString(),appDllSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
                scope="Real WinUI compositor functional checks. No frame latency, hardware performance, accessibility or media quality certification.",results,fallbackCount=motion.FallbackCount};
            File.WriteAllText(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        }
        status.Text="검증 결과를 저장했습니다.";Close();
    }
}
