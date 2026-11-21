using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CharliVision;

public enum VisionAction {
    PlayPause, Next, Previous, VolumeUp, VolumeDown, Mute,
    LyricsEarlier, LyricsLater, ToggleOverlay, OpenSettings,
    PositionMode, CycleProfile
}

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey=0x0312, ModAlt=0x1, ModControl=0x2, ModShift=0x4, ModNoRepeat=0x4000;
    private readonly AppSettings _settings;
    private readonly Action<VisionAction> _execute;
    private readonly Action<AppSettings> _persist;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly HwndSourceHook _hook;
    private readonly Dictionary<int,VisionAction> _registered=new();
    public string Status {get;private set;}="Globale Hotkeys ausgeschaltet";
    public int RegisteredCount=>_registered.Count;
    public bool Enabled=>_settings.GlobalHotkeysEnabled;
    public static IReadOnlyList<VisionAction> Actions {get;}=Enum.GetValues<VisionAction>();
    public static IReadOnlyDictionary<VisionAction,string> Defaults {get;}=
        new Dictionary<VisionAction,string> {
            [VisionAction.PlayPause]="Ctrl+Alt+P",
            [VisionAction.Next]="Ctrl+Alt+Right",
            [VisionAction.Previous]="Ctrl+Alt+Left",
            [VisionAction.VolumeUp]="Ctrl+Alt+Up",
            [VisionAction.VolumeDown]="Ctrl+Alt+Down",
            [VisionAction.Mute]="Ctrl+Alt+M",
            [VisionAction.LyricsEarlier]="Ctrl+Alt+PageUp",
            [VisionAction.LyricsLater]="Ctrl+Alt+PageDown",
            [VisionAction.ToggleOverlay]="Ctrl+Alt+V",
            [VisionAction.OpenSettings]="Ctrl+Alt+O",
            [VisionAction.PositionMode]="Ctrl+Alt+G",
            [VisionAction.CycleProfile]="Ctrl+Alt+R"
        };
    public static string Label(VisionAction action)=>action switch {
        VisionAction.PlayPause=>"Wiedergabe / Pause",
        VisionAction.Next=>"Nächster Titel",
        VisionAction.Previous=>"Vorheriger Titel",
        VisionAction.VolumeUp=>"Lauter",
        VisionAction.VolumeDown=>"Leiser",
        VisionAction.Mute=>"Stummschalten",
        VisionAction.LyricsEarlier=>"Lyrics früher",
        VisionAction.LyricsLater=>"Lyrics später",
        VisionAction.ToggleOverlay=>"Overlay zeigen / ausblenden",
        VisionAction.OpenSettings=>"Einstellungen öffnen",
        VisionAction.PositionMode=>"Positionierungsmodus aktivieren",
        VisionAction.CycleProfile=>"Animationsprofil wechseln",
        _=>action.ToString()
    };
    public HotkeyManager(Window overlay,AppSettings settings,Action<VisionAction> execute,
        Action<AppSettings>? persist=null)
    {
        _settings=settings;_execute=execute;_persist=persist??SettingsStore.Save;
        _settings.Hotkeys??=new Dictionary<string,string>();
        _hwnd=new WindowInteropHelper(overlay).Handle;
        if(_hwnd==IntPtr.Zero)throw new InvalidOperationException("Window handle unavailable");
        _source=HwndSource.FromHwnd(_hwnd);
        _hook=WndHook;
        _source?.AddHook(_hook);
        Reload();
    }
    public string Binding(VisionAction action)
    {
        if(_settings.Hotkeys.TryGetValue(action.ToString(),out string? value))
            return value??"";
        return Defaults[action];
    }
    public static string DisplayBinding(string binding)=>
        string.IsNullOrWhiteSpace(binding)?"Nicht belegt":binding;
    public static string? FromKey(Key key,ModifierKeys mods)
    {
        if(key==Key.System)key=Keyboard.IsKeyDown(Key.RightAlt)?Key.RightAlt:Key.LeftAlt;
        if(key==Key.LeftCtrl||key==Key.RightCtrl||key==Key.LeftShift||
            key==Key.RightShift||key==Key.LeftAlt||key==Key.RightAlt||
            key==Key.LWin||key==Key.RWin||key==Key.None)return null;
        if(key==Key.Escape)return "Escape";
        var parts=new List<string>();
        if(mods.HasFlag(ModifierKeys.Control))parts.Add("Ctrl");
        if(mods.HasFlag(ModifierKeys.Alt))parts.Add("Alt");
        if(mods.HasFlag(ModifierKeys.Shift))parts.Add("Shift");
        if(mods.HasFlag(ModifierKeys.Windows))parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+",parts);
    }
    private static bool TryParse(string binding,out int modifiers,out int vk,out Key key)
    {
        modifiers=0;vk=0;key=Key.None;
        if(string.IsNullOrWhiteSpace(binding))return false;
        var items=binding.Split('+',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(items.Length==0 || !Enum.TryParse(items[^1],true,out key))return false;
        for(int i=0;i<items.Length-1;i++){
            int flag=items[i].ToLowerInvariant() switch {
                "ctrl" or "control"=>ModControl, "alt"=>ModAlt, "shift"=>ModShift,_=>-1
            };
            if(flag<0 || (modifiers&flag)!=0)return false;
            modifiers|=flag;
        }
        if(items.Length>1 && modifiers==0)return false;
        vk=KeyInterop.VirtualKeyFromKey(key);
        return vk>0 && vk<=255 && key!=Key.None;
    }
    public string? Validate(VisionAction action,string binding)
    {
        if(binding=="Escape")return "Escape ist für Abbrechen reserviert.";
        if(string.IsNullOrWhiteSpace(binding))return null; // unbind
        if(binding.Contains("Win+",StringComparison.OrdinalIgnoreCase))
            return "Windows-Tastenkombinationen sind reserviert.";
        if(!TryParse(binding,out int modifiers,out _,out Key key))
            return "Unbekannte Tastenkombination.";
        if((modifiers==ModAlt&&(key==Key.Tab||key==Key.F4)) ||
           (modifiers==(ModControl|ModAlt)&&key==Key.Delete) ||
           (modifiers==(ModControl|ModShift)&&key==Key.Escape))
            return "Diese Kombination ist für Windows reserviert.";
        if(modifiers==0 && key!=Key.Space)
            return "Ohne Modifikatortaste wird nur Space im Einstellungsfenster unterstützt.";
        if(modifiers!=0 && (modifiers&(ModAlt|ModControl))==0)
            return "Bitte Strg oder Alt verwenden.";
        foreach(var other in Actions){
            if(other==action)continue;
            if(string.Equals(Binding(other),binding,StringComparison.OrdinalIgnoreCase))
                return $"Schon für {Label(other)} vergeben.";
        }
        return null;
    }
    public bool Configure(VisionAction action,string binding,out string status)
    {
        var issue=Validate(action,binding);
        if(issue!=null){status=issue;return false;}
        string original=Binding(action);
        _settings.Hotkeys[action.ToString()]=binding;
        Reload();
        if(_settings.GlobalHotkeysEnabled && binding!="Space" &&
            !string.IsNullOrEmpty(binding) && !_registered.ContainsValue(action)){
            _settings.Hotkeys[action.ToString()]=original;
            Reload();
            status="Windows hat die Kombination nicht registriert (bereits belegt?).";
            return false;
        }
        _persist(_settings);
        status=string.IsNullOrWhiteSpace(binding)?"Tastenkombination entfernt.":"Gespeichert.";
        return true;
    }
    public void Reset()
    {
        _settings.Hotkeys.Clear();
        Reload();
        _persist(_settings);
    }
    public bool SetGlobal(bool enabled,out string message)
    {
        _settings.GlobalHotkeysEnabled=enabled;
        Reload();
        _persist(_settings);
        message=Status;
        return !enabled || !Status.Contains("nicht registriert",StringComparison.Ordinal);
    }
    public void Reload()
    {
        foreach(int id in _registered.Keys.ToArray())UnregisterHotKey(_hwnd,id);
        _registered.Clear();
        if(!_settings.GlobalHotkeysEnabled){Status="Globale Hotkeys ausgeschaltet";return;}
        var errors=new List<string>();
        foreach(var action in Actions){
            var binding=Binding(action);
            if(string.IsNullOrWhiteSpace(binding)||binding=="Space")continue;
            if(Validate(action,binding)!=null){errors.Add(Label(action));continue;}
            if(!TryParse(binding,out int mod,out int vk,out _)){errors.Add(Label(action));continue;}
            int id=100+(int)action;
            if(RegisterHotKey(_hwnd,id,mod|ModNoRepeat,vk))_registered[id]=action;
            else errors.Add(Label(action));
        }
        Status=errors.Count==0?
            $"Aktiv: {_registered.Count} Kombinationen registriert":
            $"Aktiv: {_registered.Count}, nicht registriert: {string.Join(", ",errors)}";
    }
    private IntPtr WndHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg==WmHotkey && _registered.TryGetValue(wParam.ToInt32(),out var action)){
            handled=true;
            _execute(action);
        }
        return IntPtr.Zero;
    }
    public void ProcessFocusedKey(Key key,ModifierKeys modifiers)
    {
        var input=FromKey(key,modifiers);
        if(input is null)return;
        foreach(var action in Actions){
            if(Binding(action).Equals(input,StringComparison.OrdinalIgnoreCase)){
                _execute(action);return;
            }
        }
    }
    public void Dispose()
    {
        foreach(int id in _registered.Keys.ToArray())UnregisterHotKey(_hwnd,id);
        _registered.Clear();
        _source?.RemoveHook(_hook);
    }
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool RegisterHotKey(IntPtr hwnd,int id,int modifiers,int key);
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}
