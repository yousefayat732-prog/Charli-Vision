using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CharliVision;

// Reorders only the application's own top-level WPF window; never reparents Explorer.
public sealed class DesktopLayer : IDisposable
{
    private const int GWL_EXSTYLE=-20, WS_EX_NOACTIVATE=0x08000000;
    private const int WS_EX_TOOLWINDOW=0x80, WS_EX_APPWINDOW=0x40000;
    private const int GW_HWNDPREV=3;
    private const uint EVENT_SYSTEM_FOREGROUND=3, WINEVENT_OUTOFCONTEXT=0;
    private const uint SWP_NOSIZE=1, SWP_NOMOVE=2, SWP_NOACTIVATE=0x10;
    private const uint SWP_NOOWNERZORDER=0x200, SWP_FRAMECHANGED=0x20;
    private static readonly IntPtr HWND_BOTTOM=new(1), HWND_NOTOPMOST=new(-2);

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hwnd,_eventHook;
    private HwndSource? _source;
    private WinEventProc? _callback;
    private HwndSourceHook? _shellCallback;
    private readonly uint _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    private bool _pending,_disposed;

    public DesktopLayer(Window window){_window=window;_dispatcher=window.Dispatcher;}

    public void Attach()
    {
        if(_disposed||_hwnd!=IntPtr.Zero)return;
        _hwnd=new WindowInteropHelper(_window).Handle;
        if(_hwnd==IntPtr.Zero)return;
        _source=HwndSource.FromHwnd(_hwnd);
        _shellCallback=ShellMessage;
        _source?.AddHook(_shellCallback);
        _callback=ForegroundChanged;
        _eventHook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,_callback,0,0,WINEVENT_OUTOFCONTEXT);
        PlaceAboveDesktop();
    }

    private IntPtr ShellMessage(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
    {
        if((uint)message==_taskbarCreated)QueuePlacement();
        return IntPtr.Zero;
    }

    private void ForegroundChanged(IntPtr hook,uint ev,IntPtr h,int obj,int child,uint thread,uint ms)
    {
        if(!_disposed) _dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(QueuePlacement));
    }

    public void QueuePlacement()
    {
        if(_disposed||_pending||_window.Topmost||!_window.IsVisible)return;
        _pending=true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
            _pending=false;PlaceAboveDesktop();
        }));
    }

    public bool PlaceAboveDesktop()
    {
        if(_disposed||_hwnd==IntPtr.Zero||_window.Topmost||!_window.IsVisible)return false;
        try {
            int flags=GetWindowLong(_hwnd,GWL_EXSTYLE);
            flags=(flags|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW)&~WS_EX_APPWINDOW;
            SetWindowLong(_hwnd,GWL_EXSTYLE,flags);
            if((flags&0x8)!=0)
                SetWindowPos(_hwnd,HWND_NOTOPMOST,0,0,0,0,
                    SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER);

            IntPtr desktop=FindWindow("Progman",null);
            if(desktop==IntPtr.Zero) {
                // Explorer restarting: safe fallback, never manipulate external windows.
                return SetWindowPos(_hwnd,HWND_BOTTOM,0,0,0,0,
                    SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER);
            }
            IntPtr beforeDesktop=GetWindow(desktop,GW_HWNDPREV);
            if(beforeDesktop==_hwnd)return true;
            return SetWindowPos(_hwnd,beforeDesktop==IntPtr.Zero?HWND_BOTTOM:beforeDesktop,
                0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER|SWP_FRAMECHANGED);
        } catch(Exception ex){SettingsStore.Log(ex);return false;}
    }

    // Conservative occlusion check: only throttle if ONE normal opaque window
    // completely covers our own rectangle. No foreign windows are modified.
    public bool IsFullyOccluded()
    {
        if(_disposed||_hwnd==IntPtr.Zero||!_window.IsVisible)return false;
        if(!GetWindowRect(_hwnd,out var self))return false;
        if(self.Right<=self.Left||self.Bottom<=self.Top)return false;
        // Transparent input helper windows (notably Windows TextInputHost) can
        // span the desktop without visually covering it. Test actual hit-testing
        // at the vinyl center before trusting rectangle intersections.
        if((GetWindowLong(_hwnd,GWL_EXSTYLE)&0x20)!=0)return false;
        var discPoint=new POINT {
            X=self.Left+(self.Right-self.Left)*18/100,
            Y=self.Top+(self.Bottom-self.Top)/2
        };
        if(WindowFromPoint(discPoint)==_hwnd)return false;
        IntPtr window=GetWindow(_hwnd,GW_HWNDPREV);
        for(int i=0;i<180 && window!=IntPtr.Zero;i++){
            if(IsWindowVisible(window)&&!IsIconic(window) &&
                (GetWindowLong(window,GWL_EXSTYLE)&0x80000)==0 &&
                GetWindowRect(window,out var outer) &&
                outer.Left<=self.Left+3&&outer.Top<=self.Top+3 &&
                outer.Right>=self.Right-3&&outer.Bottom>=self.Bottom-3 &&
                !IsNonVisualInputWindow(window))
                return true;
            window=GetWindow(window,GW_HWNDPREV);
        }
        return false;
    }
    // Windows TextInputHost provides a screen-sized invisible input surface.
    // Its rectangle must never be interpreted as visual occlusion.
    private static bool IsNonVisualInputWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd,out uint pid);
        try {
            using var process=System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("TextInputHost",StringComparison.OrdinalIgnoreCase);
        }catch{return false;}
    }
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {public int X,Y;}
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        if(_eventHook!=IntPtr.Zero){UnhookWinEvent(_eventHook);_eventHook=IntPtr.Zero;}
        if(_source!=null && _shellCallback!=null)_source.RemoveHook(_shellCallback);
        _source=null;_callback=null;
    }
    private delegate void WinEventProc(IntPtr hook,uint ev,IntPtr h,int obj,int child,uint thread,uint ms);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string name,string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd,int cmd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd,int index,int flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(
        IntPtr hwnd,IntPtr insertAfter,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min,uint max,
        IntPtr module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr handle);
}