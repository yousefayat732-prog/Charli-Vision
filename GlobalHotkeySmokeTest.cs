using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CharliVision;

/// <summary>
/// Explicit opt-in native RegisterHotKey end-to-end QA. Registers exactly one
/// unused test chord against an in-memory config; no keyboard hook/keylogger.
/// Never changes the user's settings, hotkey registrations or media playback.
/// Does not bypass privileged or anti-cheat-protected applications.
/// </summary>
public static class GlobalHotkeySmokeTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput{
        public ushort VirtualKey,Scan;
        public uint Flags,Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion{
        [FieldOffset(0)]public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input{
        public uint Type;
        public InputUnion Data;
    }
    [DllImport("user32.dll",SetLastError=true)]
    private static extern uint SendInput(uint count,[In]Input[] inputs,int size);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);

    private static Input Key(ushort vk,bool up)=>new(){
        Type=1,
        Data=new InputUnion{
            Keyboard=new KeyboardInput{VirtualKey=vk,Flags=up?2u:0u}
        }
    };
    private static bool SendTestChord()
    {
        // CTRL+ALT+F10 (0x11,0x12,0x79), always release all three keys.
        Input[] inputs={
            Key(0x11,false),Key(0x12,false),Key(0x79,false),
            Key(0x79,true),Key(0x12,true),Key(0x11,true)
        };
        return SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())==
            (uint)inputs.Length;
    }
    private static void Pump(double seconds)
    {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(seconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    public static bool Run()
    {
        var report=new List<string>{
            "CHARLI VISION — GLOBAL HOTKEY TRUE SYSTEM SMOKE TEST",
            DateTimeOffset.Now.ToString("u"),
            "Only CTRL+ALT+F10 is emitted, no logging of physical keystrokes."
        };
        bool passed=false;
        IntPtr original=GetForegroundWindow();
        var originalPid=GetWindowThreadProcessId(original,out uint previous);
        var config=new AppSettings{GlobalHotkeysEnabled=true};
        foreach(var action in HotkeyManager.Actions)
            config.Hotkeys[action.ToString()]="";
        config.Hotkeys[VisionAction.OpenSettings.ToString()]="Ctrl+Alt+F10";
        var window=new Window{
            Title="Charli Vision Hotkey QA (no media actions)",
            Width=1,Height=1,ShowInTaskbar=false,
            WindowStyle=WindowStyle.ToolWindow
        };
        try{
            new WindowInteropHelper(window).EnsureHandle();
            int called=0;
            using var manager=new HotkeyManager(window,config,
                action=>{if(action==VisionAction.OpenSettings)called++;},
                _=>{});
            report.Add("REGISTRATION_STATUS="+manager.Status);
            if(!manager.Status.StartsWith("Aktiv: 1",StringComparison.Ordinal))
                throw new InvalidOperationException("Test chord not registered");
            var targets=new List<(string Name,IntPtr Hwnd)>();
            if(original!=IntPtr.Zero && previous!=(uint)Process.GetCurrentProcess().Id){
                try{
                    var process=Process.GetProcessById((int)previous);
                    string name=process.ProcessName;
                    if(!name.Contains("Valorant",StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("RocketLeague",StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("Roblox",StringComparison.OrdinalIgnoreCase))
                        targets.Add(("Foreground-"+name,original));
                }catch{}
            }
            foreach(string name in new[]{"chrome","Discord","Spotify"}){
                var process=Process.GetProcessesByName(name)
                    .FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero);
                if(process is null){report.Add(name.ToUpperInvariant()+"=NOT_RUNNING");continue;}
                if(targets.All(t=>t.Hwnd!=process.MainWindowHandle))
                    targets.Add((name,process.MainWindowHandle));
            }
            int verified=0;
            foreach(var target in targets.Take(4)){
                // Do not force desktop focus past normal Windows restrictions.
                if(target.Hwnd!=GetForegroundWindow() &&
                   !SetForegroundWindow(target.Hwnd)){
                    report.Add(target.Name+"=FOREGROUND_NOT_PERMITTED");
                    continue;
                }
                Pump(.18);
                if(GetForegroundWindow()!=target.Hwnd){
                    report.Add(target.Name+"=FOREGROUND_NOT_CONFIRMED");
                    continue;
                }
                int before=called;
                if(!SendTestChord()){
                    report.Add(target.Name+"=INPUT_NOT_PERMITTED");
                    continue;
                }
                Pump(.65);
                bool accepted=called==before+1;
                report.Add(target.Name+"="+(accepted?"PASS":"NO_WM_HOTKEY"));
                if(accepted)verified++;
            }
            passed=verified>0;
            report.Add("EXTERNAL_WINDOWS_TESTED="+targets.Count);
            report.Add("EXTERNAL_WINDOWS_VERIFIED="+verified);
        }catch(Exception ex){
            report.Add("FAILED="+ex.GetType().Name+": "+ex.Message);
            SettingsStore.Log(ex);
        }finally{
            if(original!=IntPtr.Zero)SetForegroundWindow(original);
            window.Close();
            report.Add("RESULT="+(passed?"PASS":"NOT_VERIFIED"));
            try{
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                    "global-hotkey-e2e.txt"),report);
            }catch{}
        }
        return passed;
    }
}
