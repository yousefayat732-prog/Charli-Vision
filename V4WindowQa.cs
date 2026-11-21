using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CharliVision;

/// <summary>
/// Opt-in real HWND/WPF settings quality assurance. Uses only an independent
/// in-memory settings instance; never changes the user's actual preferences.
/// </summary>
public static class V4WindowQa
{
    private sealed class GuiTestSkippedException(string reason):Exception(reason);
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint {public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern void mouse_event(
        uint flags,uint dx,uint dy,uint data,IntPtr extra);
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background){
            Interval=TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static T? FindVisual<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T found)return found;
            var next=FindVisual<T>(child);
            if(next is not null)return next;
        }
        return null;
    }
    private static IEnumerable<T> AllVisual<T>(DependencyObject parent)
        where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T found)yield return found;
            foreach(var nested in AllVisual<T>(child))yield return nested;
        }
    }
    private static void Wheel(UIElement source,int delta)
    {
        var args=new MouseWheelEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount,delta){
            RoutedEvent=UIElement.PreviewMouseWheelEvent,Source=source
        };
        source.RaiseEvent(args);
    }
    private static (byte Corner,byte Center,byte OtherCorner) Alpha(Window win)
    {
        win.UpdateLayout();
        int w=Math.Max(20,(int)Math.Ceiling(win.ActualWidth));
        int h=Math.Max(20,(int)Math.Ceiling(win.ActualHeight));
        var image=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
        image.Render(win);
        var pixels=new byte[w*h*4];
        image.CopyPixels(pixels,w*4,0);
        byte A(int x,int y)=>pixels[(y*w+x)*4+3];
        return (A(0,0),A(w/2,h/2),A(w-1,0));
    }
    public static bool Run()
    {
        var rows=new List<string>{
            "CHARLI VISION — V4 TRUE LIQUID GLASS / FULL SCROLL GUI QA",
            DateTimeOffset.Now.ToString("u"),
            "Dedicated live WPF windows; transient in-memory AppSettings only"
        };
        bool pass=true;
        void Check(string label,Action assert)
        {
            try{assert();rows.Add("PASS: "+label);}
            catch(GuiTestSkippedException ex){
                rows.Add("SKIP: "+label+" — "+ex.Message);
            }
            catch(Exception ex){pass=false;rows.Add("FAIL: "+label+" "+
                ex.GetType().Name+" "+ex.Message);}
        }
        static void Require(bool ok,string why)
        {
            if(!ok)throw new InvalidOperationException(why);
        }
        MainWindow? host=null;
        Window? helper=null;
        HotkeyManager? hotkeys=null;
        SettingsWindow? settingsWindow=null;
        GlassMenuWindow? menu=null;
        try{
            var settings=new AppSettings{RenderProfile="Smooth",
                GlobalHotkeysEnabled=false};
            host=new MainWindow();
            helper=new Window{ShowInTaskbar=false};
            new System.Windows.Interop.WindowInteropHelper(helper).EnsureHandle();
            hotkeys=new HotkeyManager(helper,settings,_=>{},_=>{});
            settingsWindow=new SettingsWindow(host,settings,hotkeys,
                suppressPersistenceForQa:true){
                ShowInTaskbar=false
            };
            settingsWindow.Show();
            // Put the isolated test window on the physical primary display
            // so synthetic pointer input does not target an off-screen virtual area.
            var primary=System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            settingsWindow.Left=primary.Left+Math.Max(15,(primary.Width-settingsWindow.Width)/2);
            settingsWindow.Top=primary.Top+Math.Max(15,(primary.Height-settingsWindow.Height)/2);
            Pump(400);
            var panel=settingsWindow;
            rows.Add("WPF_RENDER_TIER="+(RenderCapability.Tier>>16));
            Check("real settings HWND and transparent rounded corners",()=>{
                Require(panel.AllowsTransparency,"AllowsTransparency disabled");
                var alpha=Alpha(panel);
                rows.Add($"CORNERS=({alpha.Corner},{alpha.OtherCorner}) CENTER={alpha.Center}");
                Require(alpha.Corner<=18 && alpha.OtherCorner<=18,
                    "Rectangular opaque window corner");
                Require(alpha.Center>80,"Main window content missing");
            });
            Check("settings scrollbar orientation, bounds and linked track",()=>{
                panel.NavigateTo("Keys");
                Pump(180);
                var scroller=panel.SettingsScroller;
                scroller.ApplyTemplate();
                scroller.UpdateLayout();
                Require(scroller.ScrollableHeight>100,
                    "Keys content does not produce a scroll extent");
                var bar=FindVisual<ScrollBar>(scroller) ??
                    throw new InvalidOperationException("Vertical ScrollBar missing");
                var track=bar.Template.FindName("PART_Track",bar) as Track ??
                    throw new InvalidOperationException("Track missing");
                rows.Add($"SETTINGS_EXTENT={scroller.ExtentHeight:0.##} VIEWPORT={scroller.ViewportHeight:0.##} SCROLLABLE={scroller.ScrollableHeight:0.##}");
                rows.Add($"BAR_ORIENTATION={bar.Orientation} TRACK_ORIENTATION={track.Orientation} TRACK_MAX={track.Maximum:0.##} BAR_MAX={bar.Maximum:0.##}");
                Require(bar.Orientation==Orientation.Vertical &&
                    track.Orientation==Orientation.Vertical,"Incorrect track orientation");
                Require(Math.Abs(track.Maximum-scroller.ScrollableHeight)<2,
                    "Track maximum not bound to scrollable height");
            });
            Check("real WPF mouse-wheel scrolls downward and upward",()=>{
                var scroller=panel.SettingsScroller;
                scroller.ScrollToTop();Pump(80);
                Wheel(panel.SettingsContent,-120);
                Pump(300);
                double down=scroller.VerticalOffset;
                Wheel(panel.SettingsContent,120);Pump(300);
                double back=scroller.VerticalOffset;
                rows.Add($"MOUSE_WHEEL_DOWN={down:0.##} BACK={back:0.##}");
                Require(down>20 && back<4,"Wheel input did not move both directions");
            });
            Check("native scrollbar thumb value actually moves viewport",()=>{
                var scroller=panel.SettingsScroller;
                var bar=FindVisual<ScrollBar>(scroller)!;
                scroller.ScrollToTop();Pump(80);
                bool canPageDown=ScrollBar.PageDownCommand.CanExecute(null,bar);
                rows.Add("SCROLLBAR_PAGE_DOWN_CAN_EXECUTE="+canPageDown);
                if(canPageDown)ScrollBar.PageDownCommand.Execute(null,bar);
                Pump(110);
                var moved=scroller.VerticalOffset;
                bool canPageUp=ScrollBar.PageUpCommand.CanExecute(null,bar);
                rows.Add("SCROLLBAR_PAGE_UP_CAN_EXECUTE="+canPageUp);
                if(canPageUp)ScrollBar.PageUpCommand.Execute(null,bar);
                Pump(110);
                rows.Add($"SCROLLBAR_MOVED={moved:0.##} RESET={scroller.VerticalOffset:0.##}");
                Require(moved>scroller.ScrollableHeight*.55,
                    "ScrollBar.Value did not move ScrollViewer");
                Require(scroller.VerticalOffset<5,"ScrollBar did not return to top");
            });
            Check("physical mouse drag of native scrollbar thumb",()=>{
                var scroll=panel.SettingsScroller;
                scroll.ScrollToTop();Pump(90);
                var bar=FindVisual<ScrollBar>(scroll)!;
                var thumb=FindVisual<Thumb>(bar);
                Require(thumb is not null && thumb.ActualHeight>8,
                    "Physical scrollbar thumb not laid out");
                var p=thumb!.PointToScreen(
                    new Point(thumb.ActualWidth/2,thumb.ActualHeight/2));
                int x=(int)Math.Round(p.X), y=(int)Math.Round(p.Y);
                int dragStarted=0,dragDelta=0;
                thumb.DragStarted+=(_,_)=>dragStarted++;
                thumb.DragDelta+=(_,_)=>dragDelta++;
                rows.Add($"THUMB_SIZE={thumb.ActualWidth:0.#}x{thumb.ActualHeight:0.#} BAR_SIZE={bar.ActualWidth:0.#}x{bar.ActualHeight:0.#} SCREEN={x},{y}");
                int travel=(int)Math.Clamp(bar.ActualHeight-thumb.ActualHeight,
                    35,145);
                IntPtr window=new System.Windows.Interop.WindowInteropHelper(panel).Handle;
                IntPtr previous=GetForegroundWindow();
                _=GetCursorPos(out var original);
                bool foreground=GetForegroundWindow()==window ||
                    SetForegroundWindow(window);
                rows.Add("DRAG_FOCUS_GRANTED="+foreground);
                if(!foreground)
                    throw new GuiTestSkippedException(
                        "Windows foreground restrictions denied pointer test");
                try{
                    Require(SetCursorPos(x,y),"OS denied positioning test pointer");
                    _=GetCursorPos(out var placed);
                    rows.Add($"ACTUAL_CURSOR={placed.X},{placed.Y} EXPECTED={x},{y}");
                    Pump(70);
                    mouse_event(0x0002,0,0,0,IntPtr.Zero);
                    Pump(85);
                    rows.Add($"THUMB_MOUSE_OVER={thumb.IsMouseOver} CAPTURE={thumb.IsMouseCaptured} BUTTON={Mouse.LeftButton}");
                    for(int i=1;i<=7;i++){
                        _=SetCursorPos(x,y+travel*i/7);
                        Pump(40);
                    }
                    mouse_event(0x0004,0,0,0,IntPtr.Zero);
                    Pump(120);
                    rows.Add($"PHYSICAL_THUMB_DRAG={scroll.VerticalOffset:0.##} TRAVEL_PX={travel} START={dragStarted} DELTA={dragDelta}");
                    Require(scroll.VerticalOffset>scroll.ScrollableHeight*.18,
                        "Dragging visible thumb did not move content");
                }finally{
                    mouse_event(0x0004,0,0,0,IntPtr.Zero);
                    _=SetCursorPos(original.X,original.Y);
                    if(previous!=IntPtr.Zero)SetForegroundWindow(previous);
                }
            });
            Check("rapid alternating wheel actions remain interruptible",()=>{
                var scroller=panel.SettingsScroller;
                scroller.ScrollToTop();Pump(100);
                Wheel(panel.SettingsContent,-120);
                Wheel(panel.SettingsContent,-120);
                Wheel(panel.SettingsContent,120);
                Pump(310);
                rows.Add($"FAST_WHEEL_FINAL={scroller.VerticalOffset:0.##}");
                Require(scroller.VerticalOffset>20 &&
                    scroller.VerticalOffset<scroller.ScrollableHeight,
                    "Wheel target stalled or overflowed");
            });
            Check("six categories accessible after scroll; scrolling resets",()=>{
                var scroller=panel.SettingsScroller;
                foreach(var section in new[]{"Playback","Lyrics","Layout","Design","Keys","App"}){
                    panel.NavigateTo(section);
                    Pump(115);
                    Require(panel.SettingsContent.Children.Count>0,
                        section+" page empty");
                    Require(scroller.VerticalOffset<3,
                        section+" inherited previous page scroll");
                    scroller.ScrollToBottom();Pump(50);
                    Require(scroller.VerticalOffset<=scroller.ScrollableHeight+1,
                        section+" scroll exceeded content range");
                }
            });
            Check("small settings window still shows a scrollable Keys page",()=>{
                panel.Height=510;
                panel.Width=760;
                panel.NavigateTo("Keys");
                Pump(160);
                Require(panel.SettingsScroller.ViewportHeight>160 &&
                    panel.SettingsScroller.ScrollableHeight>200,
                    "Small layout clipped instead of scrollable");
                var right=panel.SettingsScroller.ViewportWidth;
                var buttons=AllVisual<Button>(panel.SettingsContent)
                    .Where(b=>b.Content is string caption &&
                        (caption=="Ändern"||caption=="Entfernen")).ToArray();
                Require(buttons.Length>=20,"Keybinding action buttons absent");
                foreach(var button in buttons){
                    var end=button.TranslatePoint(new Point(button.ActualWidth,0),
                        panel.SettingsScroller);
                    if(end.X>right+1 || button.ActualWidth<50)
                        throw new InvalidOperationException(
                            "Keybind action is unreachable at narrow width: "+end.X);
                }
                rows.Add($"NARROW_BUTTONS_IN_VIEW={buttons.Length} VIEWPORT_WIDTH={right:0.##}");
            });
            Check("category switching completes quickly on this hardware",()=>{
                var stopwatch=System.Diagnostics.Stopwatch.StartNew();
                var timings=new List<double>();
                foreach(var section in new[]{"Playback","Lyrics","Layout","Design","Keys","App",
                    "Playback","Lyrics","Layout","Design","Keys","App"}){
                    stopwatch.Restart();
                    panel.NavigateTo(section);panel.UpdateLayout();
                    timings.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                timings.Sort();
                rows.Add($"CATEGORY_SWITCH_MEAN_MS={timings.Average():0.##} MAX_MS={timings[^1]:0.##}");
                Require(timings[^1]<250,
                    "Category switching blocked the UI for >=250ms");
            });
            menu=new GlassMenuWindow(host,settings,hotkeys,new Point(380,320));
            menu.Height=490; // Simulated reduced monitor work area
            menu.Show();Pump(300);
            var compact=menu;
            Check("compact menu true-alpha corners",()=>{
                var alpha=Alpha(compact);
                rows.Add($"MENU_CORNERS=({alpha.Corner},{alpha.OtherCorner})");
                Require(compact.AllowsTransparency && alpha.Corner<=18 &&
                    alpha.OtherCorner<=18,"Compact menu corners not transparent");
            });
            Check("compact menu remains mouse-wheel scrollable on short screens",()=>{
                var scroller=compact.MenuScroller;
                scroller.UpdateLayout();
                Require(scroller.ScrollableHeight>100,
                    "Compact menu content not scrollable");
                Wheel(compact.MenuItems,-120);Pump(320);
                double down=scroller.VerticalOffset;
                Wheel(compact.MenuItems,120);Pump(320);
                rows.Add($"COMPACT_WHEEL_DOWN={down:0.##} BACK={scroller.VerticalOffset:0.##}");
                Require(down>20&&scroller.VerticalOffset<5,
                    "Compact menu wheel down/up failed");
            });
            rows.Add("RESULT="+(pass?"PASS":"FAIL"));
        }catch(Exception ex){
            pass=false;
            rows.Add("FATAL="+ex.GetType().Name+" "+ex.Message);
            rows.Add("RESULT=FAIL");
        }finally{
            try{menu?.Close();}catch{}
            try{settingsWindow?.Close();}catch{}
            try{hotkeys?.Dispose();}catch{}
            try{helper?.Close();}catch{}
            try{host?.Close();}catch{}
            try{
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                    "v4-window-gui-qa.txt"),rows);
            }catch{}
        }
        return pass;
    }
}
