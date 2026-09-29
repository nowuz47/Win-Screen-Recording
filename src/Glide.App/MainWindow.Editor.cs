using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Glide.Core;
using Glide.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Glide.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? playback;
    private bool advancingPlayback, compareOriginal;
    private string? selectedZoom, inspectorSelection;
    private sealed class ZoomBlockView
    {
        public required Button Button;
        public required string ZoomId;
        public long OutputStart;
        public double X, Y;
    }
    private readonly Dictionary<string, ZoomBlockView> zoomViews = [];
    private TextBlock? emptyZooms;
    private WriteableBitmap? playbackBitmap;
    private static readonly uint[] Backgrounds = [0xFF141820, 0xFF162B48, 0xFFF0EEE8];

    private MediaRenderer CreateRenderer(ProjectDocument document, CursorTrack track)
    {
        var (w,h) = document.Aspect switch { "1:1" => (1080,1080), "9:16" => (1080,1920), _ => (1920,1080) };
        return new(store,document,track,w,h);
    }
    private void RefreshInspector()
    {
        if (edits is null) return;
        var p = edits.Current; var style = p.Style ?? new();
        RefreshAudioInspector(p);
        PaddingValue.Value = style.Padding*100; CornerValue.Value = style.CornerRadius; CursorScaleValue.Value = style.CursorScale;
        SmoothCursorValue.IsChecked = style.SmoothCursor; ShowCursorValue.IsChecked = style.ShowCursor; ShowClicksValue.IsChecked = style.ShowClicks;
        BackgroundChoice.SelectedIndex = Math.Max(0,Array.IndexOf(Backgrounds,style.Background));
        AspectChoice.SelectedIndex = p.Aspect switch { "1:1" => 1, "9:16" => 2, _ => 0 };
        var zoom = p.Zooms.FirstOrDefault(z=>z.Id==selectedZoom);
        if (zoom is null)
        {
            ZoomInspector.IsEnabled = false;
            if (ZoomInspector.Visibility == Visibility.Visible)
                uiMotion?.Exit(ZoomInspector, () => ZoomInspector.Visibility = Visibility.Collapsed);
        }
        else
        {
            bool changed = inspectorSelection != zoom.Id;
            if (changed) uiMotion?.Cancel(ZoomInspector);
            ZoomInspector.Visibility = Visibility.Visible; ZoomInspector.IsEnabled = true;
            if (changed) uiMotion?.Enter(ZoomInspector, MotionTokens.Panel, x: 12, y: 0);
        }
        inspectorSelection = zoom?.Id;
        if (zoom is not null)
        {
            ZoomSelectionLabel.Text = "미리보기를 클릭해 초점 이동"; ZoomScale.Value=zoom.Scale;
            ZoomStart.Maximum=ZoomEnd.Maximum=p.DurationUs/1_000_000.0;
            ZoomStart.Value=zoom.Range.StartUs/1_000_000.0; ZoomEnd.Value=zoom.Range.EndUs/1_000_000.0; ZoomLocked.IsChecked=zoom.Locked;
        }
    }
    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width-e.PreviousSize.Width) > 1) RebuildZoomBlocks();
    }
    private void RebuildZoomBlocks()
    {
        if(edits is null || ZoomBlocks.ActualWidth < 1)return;
        var timeline = new Timeline(edits.Current.Ranges); double width=ZoomBlocks.ActualWidth;
        List<double> laneEnds=[]; HashSet<string> retainedKeys=[];
        foreach(var zoom in edits.Current.Zooms)
        {
            long offset=0;
            foreach(var retained in timeline.Ranges)
            {
                long start=Math.Max(zoom.Range.StartUs,retained.StartUs),end=Math.Min(zoom.Range.EndUs,retained.EndUs);
                if(end>start)
                {
                    long outputStart=offset+start-retained.StartUs;
                    double x=outputStart/(double)timeline.DurationUs*width;
                    double w=Math.Min(width-x,Math.Max(28,(end-start)/(double)timeline.DurationUs*width));
                    int lane=laneEnds.FindIndex(last=>last+4<=x);if(lane<0){lane=laneEnds.Count;laneEnds.Add(0);}laneEnds[lane]=x+w;
                    string key=$"{zoom.Id}:{retained.StartUs}"; retainedKeys.Add(key);
                    bool added = !zoomViews.TryGetValue(key, out var view);
                    if (added)
                    {
                        var button = new Button {Height=32,Padding=new Thickness(3)};
                        view = new() {Button=button,ZoomId=zoom.Id,OutputStart=outputStart,X=x,Y=lane*38};
                        var selectedView=view;
                        button.Click+=(_,_)=>{selectedZoom=selectedView.ZoomId;Playhead.Value=selectedView.OutputStart/1_000_000.0;RefreshInspector();RebuildZoomBlocks();};
                        zoomViews[key]=view; ZoomBlocks.Children.Add(button); uiMotion?.AttachButton(button);
                    }
                    var block=view!; double oldX=block.X,oldY=block.Y;
                    block.OutputStart=outputStart;block.X=x;block.Y=lane*38;
                    block.Button.Width=w;block.Button.Content=w>60?$"{zoom.Scale:0.0}×":"•";
                    Canvas.SetLeft(block.Button,x);Canvas.SetTop(block.Button,block.Y);
                    string label=$"확대 {start/1_000_000.0:0.0}–{end/1_000_000.0:0.0}초 · {zoom.Scale:0.0}배";
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(block.Button,label);ToolTipService.SetToolTip(block.Button,label);
                    if(zoom.Id==selectedZoom)block.Button.Style=(Style)Application.Current.Resources["GlidePrimaryButton"];
                    else block.Button.ClearValue(FrameworkElement.StyleProperty);
                    if(added)uiMotion?.Enter(block.Button,MotionTokens.Panel,y:8,scale:.97f);
                    else if(Math.Abs(oldX-x)>.5||Math.Abs(oldY-block.Y)>.5)uiMotion?.Move(block.Button,(float)(oldX-x),(float)(oldY-block.Y));
                }
                offset+=retained.DurationUs;
            }
        }
        foreach(var key in zoomViews.Keys.Where(k=>!retainedKeys.Contains(k)).ToArray())
        {
            var removed=zoomViews[key];zoomViews.Remove(key);removed.Button.IsEnabled=false;removed.Button.IsHitTestVisible=false;
            void Remove(){uiMotion?.DetachButton(removed.Button);ZoomBlocks.Children.Remove(removed.Button);UpdateEmptyZoomVisibility();}
            if(uiMotion is null)Remove();else uiMotion.Exit(removed.Button,Remove);
        }
        ZoomBlocks.Height=Math.Max(40,laneEnds.Count*38);
        if(emptyZooms is null){emptyZooms=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,IsHitTestVisible=false};ZoomBlocks.Children.Add(emptyZooms);}
        emptyZooms.Width=width;UpdateEmptyZoomVisibility();
        emptyZooms.Text=edits.Current.Origin=="presentation"&&edits.Current.UsePresentationCamera?"녹화 당시 커서 구도 사용 · 수동 확대를 추가할 수 있습니다.":"확대 없음 · 필요한 위치에 확대를 추가하세요.";
    }

    private void UpdateEmptyZoomVisibility()
    {
        // Wait for the final outgoing block; the empty hint must not overlap its fading label.
        if(emptyZooms is not null)emptyZooms.Visibility=zoomViews.Count==0&&!ZoomBlocks.Children.OfType<Button>().Any()?Visibility.Visible:Visibility.Collapsed;
    }

    private async void ApplyStyle(object sender,RoutedEventArgs e)
    {
        try
        {
            var style=new RenderStyle(PaddingValue.Value/100,CursorScaleValue.Value,SmoothCursorValue.IsChecked==true,ShowCursorValue.IsChecked==true,
                Backgrounds[Math.Max(0,BackgroundChoice.SelectedIndex)],CornerValue.Value,ShowClicksValue.IsChecked==true);
            style.Validate();string aspect=AspectChoice.SelectedIndex switch {1=>"1:1",2=>"9:16",_=>"16:9"};
            await ChangeEdit(h=>h.Apply(p=>p with {Style=style,Aspect=aspect},store.Save));
        }
        catch(ArgumentException){Show("스타일 값을 확인해 주세요.",InfoBarSeverity.Warning);}
    }
    private async void SaveZoom(object sender,RoutedEventArgs e)
    {
        if(selectedZoom is null)return;
        try
        {
            if(!double.IsFinite(ZoomStart.Value)||!double.IsFinite(ZoomEnd.Value))throw new ArgumentException();
            var range=new TimeRange((long)Math.Round(ZoomStart.Value*1_000_000),(long)Math.Round(ZoomEnd.Value*1_000_000));
            double scale=ZoomScale.Value;bool locked=ZoomLocked.IsChecked==true;string id=selectedZoom;
            await ChangeEdit(h=>h.Apply(p=>p with {Zooms=p.Zooms.Select(z=>z.Id==id ? z with {Range=range,Scale=scale,Locked=locked}:z).OrderBy(z=>z.Range.StartUs).ToArray()},store.Save));
        }
        catch(ArgumentException){Show("확대 구간과 배율을 확인해 주세요.",InfoBarSeverity.Warning);}
    }
    private async void DeleteZoom(object sender,RoutedEventArgs e)
    {
        if(selectedZoom is null)return;string id=selectedZoom;selectedZoom=null;
        await ChangeEdit(h=>h.Apply(p=>p with {Zooms=p.Zooms.Where(z=>z.Id!=id).ToArray()},store.Save));
    }
    private async void AnalyzeZooms(object sender,RoutedEventArgs e)
    {
        if(edits is null)return;
        var p=edits.Current;
        var pointers=CaptureImport.ReadPointers(Path.Combine(store.ProjectPath(p.Id),"capture"),p.DurationUs);
        await ChangeEdit(h=>h.Apply(project=>project with {Zooms=Motion.Plan(pointers.Clicks,project.DurationUs,project.Zooms).ToArray(),UsePresentationCamera=false},store.Save));
    }
    private async void MoveZoomFocus(object sender,PointerRoutedEventArgs e)
    {
        if(selectedZoom is null||renderer is null||edits is null||busy)return;
        var plan=renderer.Plan;var point=e.GetCurrentPoint(EditorPreview).Position;
        double scale=Math.Min(EditorPreview.ActualWidth/plan.Width,EditorPreview.ActualHeight/plan.Height);
        if(scale<=0)return;
        double x=(point.X-(EditorPreview.ActualWidth-plan.Width*scale)/2)/scale;
        double y=(point.Y-(EditorPreview.ActualHeight-plan.Height*scale)/2)/scale;
        var f=plan.AtTime(Math.Clamp((long)(Playhead.Value*1_000_000),0,plan.DurationUs-1));
        if(!f.Destination.Contains(new(x,y)))return;
        var focus=new PointD(Math.Clamp(f.SourceCrop.X+(x-f.Destination.X)/f.Destination.Width*f.SourceCrop.Width,0,1),
            Math.Clamp(f.SourceCrop.Y+(y-f.Destination.Y)/f.Destination.Height*f.SourceCrop.Height,0,1));
        string id=selectedZoom;
        await ChangeEdit(h=>h.Apply(p=>p with {Zooms=p.Zooms.Select(z=>z.Id==id?z with {Focus=focus,Locked=true}:z).ToArray()},store.Save));
    }
    private void CompareOriginal(object sender,RoutedEventArgs e)
    {
        compareOriginal=!compareOriginal;SetCommand(CompareButton,compareOriginal?"편집 결과 보기":"원본 비교", "\uE8A5");
        QueuePreview();
    }
    private void StopPlayback()
    {
        playback?.Cancel();player.Pause();if(PlayButton is not null)SetCommand(PlayButton,"재생","\uE768");
        if(audioPreview is not null){player.Source=null;var previous=audioPreview;audioPreview=null;previous.Dispose();}
    }
    private async void TogglePlayback(object sender,RoutedEventArgs e)
    {
        if(playback is not null){StopPlayback();return;}
        if(renderer is null||busy||loadingProject)return;
        previewRequest?.Cancel();var snapshot=renderer;using var request=playback=new CancellationTokenSource();var token=request.Token;
        SetCommand(PlayButton,"일시정지","\uE769");Preview.Visibility=Visibility.Collapsed;EditorPreview.Visibility=Visibility.Visible;
        long start=(long)(Playhead.Value*1_000_000);if(start>=snapshot.Plan.DurationUs-100_000)start=0;
        var watch=Stopwatch.StartNew();
        AudioPreviewSource? sound=null;
        try
        {
            var document=edits?.Current;
            if(document?.Audio?.Any(t=>t.Enabled&&t.Gain>0)==true)
            {
                sound=await Task.Run(()=>new AudioPreviewSource(store,document,start),token);
                if(token.IsCancellationRequested){sound.Dispose();return;}
                PrepareAudioPlayer(request);audioPreview=sound;player.Source=sound.Source;player.Play();
            }
            while(!token.IsCancellationRequested && renderer==snapshot)
            {
                if(sound?.Failure is Exception audioError)throw audioError;
                long time=Math.Clamp(sound is null?start+watch.Elapsed.Ticks/10:player.PlaybackSession.Position.Ticks/10,0,snapshot.Plan.DurationUs-1);
                await previewGate.WaitAsync(token);
                byte[] pixels;
                try{pixels=await Task.Run(()=>snapshot.Preview(time,compareOriginal),token);}finally{previewGate.Release();}
                if(token.IsCancellationRequested||renderer!=snapshot)break;
                if(playbackBitmap is null||playbackBitmap.PixelWidth!=snapshot.Plan.Width||playbackBitmap.PixelHeight!=snapshot.Plan.Height)
                    playbackBitmap=new(snapshot.Plan.Width,snapshot.Plan.Height);
                using(var stream=playbackBitmap.PixelBuffer.AsStream())await stream.WriteAsync(pixels,token);
                playbackBitmap.Invalidate();EditorPreview.Source=playbackBitmap;
                advancingPlayback=true;Playhead.Value=time/1_000_000.0;advancingPlayback=false;
                PlayheadText.Text=TimeSpan.FromMicroseconds(time).ToString(@"mm\:ss\.ff");
                if(time>=snapshot.Plan.DurationUs-1)break;
                await Task.Delay(16,token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Show($"미리보기를 재생하지 못했습니다. ({ex.HResult:X8})",InfoBarSeverity.Error);}
        finally
        {
            if(sound is not null)
            {
                try { if(audioPreview==sound){player.Pause();player.Source=null;audioPreview=null;} }
                finally { sound.Dispose(); }
            }
            if(playback==request)playback=null;SetCommand(PlayButton,"재생","\uE768");
        }
    }
}
