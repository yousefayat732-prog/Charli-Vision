using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CharliVision;

/// <summary>
/// Native Windows 11 DWM material hint with a WPF-only material fallback.
/// Does not alter layered desktop-overlay windows or request any screen capture.
/// </summary>
internal static class GlassWindowMaterial
{
    [DllImport("dwmapi.dll",PreserveSig=true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,int attribute,ref int value,int valueBytes);

    internal static bool Apply(Window window,bool transient=false)
    {
        // DWM backdrop attributes do not apply reliably to layered WPF windows.
        // Preserve their true-alpha rounded corners rather than forcing a second layer.
        if(window.AllowsTransparency ||
            !OperatingSystem.IsWindowsVersionAtLeast(10,0,22621))return false;
        try{
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            if(hwnd==IntPtr.Zero)return false;
            int value=1; // DWMWA_USE_IMMERSIVE_DARK_MODE
            _=DwmSetWindowAttribute(hwnd,20,ref value,sizeof(int));
            value=2; // DWMWCP_ROUND
            _=DwmSetWindowAttribute(hwnd,33,ref value,sizeof(int));
            value=transient?3:2; // DWMSBT_TRANSIENTWINDOW / MAINWINDOW
            return DwmSetWindowAttribute(hwnd,38,ref value,sizeof(int))==0;
        }catch(DllNotFoundException){return false;}
        catch(EntryPointNotFoundException){return false;}
        catch(ArgumentException){return false;}
    }

    private static Color Tone(Color color,int alpha,double towardNeutral=.14)
    {
        var c=AlbumPalette.Mix(Color.FromRgb(23,34,46),color,towardNeutral);
        return Color.FromArgb((byte)alpha,c.R,c.G,c.B);
    }
    private static LinearGradientBrush Gradient(Color a,Color b)
    {
        var brush=new LinearGradientBrush(a,b,new Point(0,0),new Point(1,1));
        brush.Freeze();
        return brush;
    }
    internal static Brush Header(ChromaticTokens t)=>
        Gradient(Tone(t.Accent,176,.18),Tone(t.Palette.Secondary,161,.08));
    internal static Brush Sidebar(ChromaticTokens t)=>
        Gradient(Tone(t.Palette.Secondary,149,.15),
            Color.FromArgb(131,16,30,40));
    internal static Brush Footer(ChromaticTokens t)=>
        Gradient(Tone(t.Accent,165,.12),Color.FromArgb(142,18,33,42));
    internal static Brush Card(ChromaticTokens t)=>
        Gradient(Tone(t.Accent,142,.12),Color.FromArgb(129,25,41,51));
    internal static Brush Button(ChromaticTokens t)=>
        Gradient(Tone(t.Accent,131,.16),Color.FromArgb(123,46,67,82));
    internal static Brush IconTile(ChromaticTokens t)=>
        Gradient(Color.FromArgb(122,t.Palette.Highlight.R,
            t.Palette.Highlight.G,t.Palette.Highlight.B),
            Tone(t.Accent,128,.14));
    internal static Brush Ambient(ChromaticTokens t)
    {
        var accent=t.Accent;
        var brush=new RadialGradientBrush{
            Center=new Point(.64,.20),GradientOrigin=new Point(.64,.20),
            RadiusX=.79,RadiusY=.88,
            GradientStops=new GradientStopCollection{
                new GradientStop(Color.FromArgb(78,accent.R,accent.G,accent.B),0),
                new GradientStop(Color.FromArgb(25,accent.R,accent.G,accent.B),.43),
                new GradientStop(Colors.Transparent,1)
            }
        };
        brush.Freeze();return brush;
    }
}
