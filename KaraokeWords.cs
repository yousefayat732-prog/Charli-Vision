using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CharliVision;

/// <summary>Native WPF word-synchronized renderer with reusable gradient and no DOM/WebView.</summary>
public sealed class KaraokeWords
{
    private sealed record WordItem(LyricWord Timing,TextBlock Text,ScaleTransform Scale);
    private readonly WrapPanel _panel;
    private readonly List<WordItem> _items=new();
    // Restrained glyph-only light; the previous 15px blur formed color blobs.
    private readonly DropShadowEffect _glow=new(){BlurRadius=3.6,Opacity=.18,ShadowDepth=0};
    // Only the ACTIVE word uses this mutable gradient; the others share frozen brushes.
    private readonly GradientStop _sweepFilled=new(Colors.White,.1);
    private readonly GradientStop _sweepPending=new(Colors.Gray,.15);
    private readonly GradientStop _sweepStart=new(Colors.White,0);
    private readonly GradientStop _sweepEnd=new(Colors.Gray,1);
    private readonly LinearGradientBrush _sweep;
    private int _active=-999,_lastStarted=-999;
    private double _lastScale=1,_lastProgress=-1;
    private SolidColorBrush _pending=Brush(Color.FromRgb(181,184,194));
    private SolidColorBrush _past=Brush(Color.FromRgb(236,240,247));
    private bool _reducedMotion;
    private AlbumPalette? _lastAppliedTheme;
    private bool _lastAppliedHighContrast;
    private double _glowStrength=.54;
    public double SizeScale {get;set;}=1;
    public bool HighContrast {get;set;}
    internal static double VisualGlowOpacity(double strength)=>
        Math.Clamp(strength,0,1)*.28;
    public double GlowStrength {get=>_glowStrength;set{
        double next=Math.Clamp(value,0,1);
        if(Math.Abs(next-_glowStrength)<.0001)return;
        _glowStrength=next;
        _glow.Opacity=VisualGlowOpacity(_glowStrength);
    }}
    public int CurrentWordIndex=>_active>=0?_active:-1;
    public bool ReducedMotion {
        get=>_reducedMotion;
        set { _reducedMotion=value; }
    }

    public KaraokeWords(WrapPanel panel)
    {
        _panel=panel;
        _sweep=new LinearGradientBrush {
            StartPoint=new Point(0,0),EndPoint=new Point(1,0),
            GradientStops=new GradientStopCollection{
                _sweepStart,_sweepFilled,_sweepPending,_sweepEnd
            }
        };
    }
    private static SolidColorBrush Brush(Color color)
    {
        var b=new SolidColorBrush(color);b.Freeze();return b;
    }
    public bool Setup(LyricLine line)
    {
        Clear();
        if(line.Words is not {Count:>0} words || words.Count>70 || line.Text.Length>170)
            return false;
        double size=line.Text.Length switch{
            <25=>49,<45=>41,<70=>34,<105=>29,_=>25
        };
        foreach(var timed in words){
            if(string.IsNullOrWhiteSpace(timed.Text))continue;
            string visible=timed.Text.Trim();
            if(visible.Length==0)continue;
            double gap=timed.Text.EndsWith(" ",StringComparison.Ordinal)?
                Math.Max(7,size*.20):1.5;
            var scale=new ScaleTransform(1,1);
            var block=new TextBlock{
                Text=visible,
                Foreground=_pending,
                Opacity=HighContrast?.90:.80,
                FontFamily=new FontFamily("Segoe UI Variable Display,Segoe UI"),
                FontWeight=FontWeights.SemiBold,
                FontSize=size*Math.Clamp(SizeScale,.7,1.4),
                TextWrapping=TextWrapping.NoWrap,
                Margin=new Thickness(0,0,gap,3),
                RenderTransform=scale,
                RenderTransformOrigin=new Point(.5,.5)
            };
            _panel.Children.Add(block);
            _items.Add(new WordItem(timed,block,scale));
        }
        if(_items.Count==0)return false;
        _panel.Visibility=Visibility.Visible;
        _active=-999;_lastStarted=-999;_lastProgress=-1;
        return true;
    }
    public void ApplyTheme(AlbumPalette palette)
    {
        // Album transitions can deliver identical quantized colors for many
        // compositor frames. Reuse the frozen brushes and glyph layout.
        if(_lastAppliedTheme is AlbumPalette last && last==palette &&
            _lastAppliedHighContrast==HighContrast)return;
        _lastAppliedTheme=palette;
        _lastAppliedHighContrast=HighContrast;
        _pending=HighContrast?
            Brush(Color.FromRgb(226,231,239)):
            Brush(AlbumPalette.Mix(Color.FromRgb(185,187,194),
                palette.Secondary,.11));
        _past=HighContrast?
            Brush(Color.FromRgb(253,252,252)):
            Brush(AlbumPalette.Mix(Color.FromRgb(247,246,246),
                palette.Highlight,.20));
        var accent=AlbumPalette.Mix(Color.FromRgb(255,250,250),palette.Accent,.18);
        var bright=AlbumPalette.Mix(Color.FromRgb(255,255,255),palette.Highlight,.12);
        _sweepStart.Color=bright;
        _sweepFilled.Color=accent;
        _sweepPending.Color=_pending.Color;
        _sweepEnd.Color=_pending.Color;
        _glow.Color=palette.Accent;
        // Preserve the current word and its progressive-fill position during
        // the album-color crossfade. Resetting these each frame made the word
        // repeatedly reinitialize and caused visible stuttering.
        for(int i=0;i<_items.Count;i++){
            if(i==_active)continue;
            _items[i].Text.Foreground=i<=_lastStarted?_past:_pending;
        }
    }
    public void Update(TimeSpan position)
    {
        if(_items.Count==0)return;
        int latest=-1;
        for(int i=0;i<_items.Count;i++) {
            if(_items[i].Timing.Start<=position)latest=i;
            else break;
        }
        int active=latest;
        if(active>=0 && _items[active].Timing.End is TimeSpan ending &&
            position>=ending)active=-1;
        if(active!=_active||latest!=_lastStarted) {
            _active=active;_lastStarted=latest;_lastProgress=-1;
            for(int i=0;i<_items.Count;i++){
                var part=_items[i];
                part.Text.Foreground=i==active?_sweep:(i<=latest?_past:_pending);
                part.Text.Effect=i==active?_glow:null;
                double targetOpacity=i==active?1:
                    HighContrast?(i<=latest?.97:.90):(i<=latest?.93:.80);
                if(_reducedMotion){
                    part.Text.BeginAnimation(UIElement.OpacityProperty,null);
                    part.Text.Opacity=targetOpacity;
                }else if(Math.Abs(part.Text.Opacity-targetOpacity)>.015){
                    // Opacity eases between word states; highlighting is still
                    // driven immediately by real timestamps, never delayed.
                    var ease=new DoubleAnimation(targetOpacity,
                        TimeSpan.FromMilliseconds(i==active?55:75)){
                        EasingFunction=new QuadraticEase{
                            EasingMode=EasingMode.EaseOut}
                    };
                    part.Text.BeginAnimation(UIElement.OpacityProperty,ease);
                }
                if(i!=active){part.Scale.ScaleX=1;part.Scale.ScaleY=1;}
            }
            _lastScale=1;
        }
        if(active<0)return;
        var current=_items[active];
        double ratio;
        bool timed=current.Timing.End is TimeSpan end &&
            end>current.Timing.Start;
        if(timed){
            double length=(current.Timing.End!.Value-current.Timing.Start).TotalMilliseconds;
            ratio=Math.Clamp((position-current.Timing.Start).TotalMilliseconds/length,0,1);
        }else ratio=.5; // display emphasis only, no claimed precise word end
        if(!timed || Math.Abs(ratio-_lastProgress)>.012) {
            _lastProgress=ratio;
            double sweep=timed?Math.Clamp(.06+ratio*.91,0,1):.8;
            // A wider feather softens the fill edge *within glyphs*;
            // never paint a rectangular background behind the word.
            _sweepFilled.Offset=Math.Clamp(sweep-.09,0,1);
            _sweepPending.Offset=Math.Clamp(sweep+.09,0,1);
        }
        _glow.Opacity=VisualGlowOpacity(_glowStrength)*
            (.75+.25*Math.Sin(Math.PI*ratio));
        double scale=_reducedMotion?1: (timed?
            1.012+.025*Math.Sin(Math.PI*Math.Pow(ratio,.75)):1.025);
        if(Math.Abs(scale-_lastScale)>.003) {
            _lastScale=scale;
            current.Scale.ScaleX=scale;
            current.Scale.ScaleY=scale;
        }
    }
    public void Clear()
    {
        foreach(var part in _items)part.Text.Effect=null;
        _items.Clear();
        _panel.Children.Clear();
        _panel.Visibility=Visibility.Collapsed;
        _active=-999;_lastStarted=-999;_lastProgress=-1;_lastScale=1;
    }

    internal void SetContext()
    {
        foreach (var part in _items)
        {
            part.Text.BeginAnimation(UIElement.OpacityProperty,null);
            part.Text.Foreground=_pending;
            part.Text.Effect=null;
            part.Text.Opacity=HighContrast?.90:.80;
            part.Scale.ScaleX=part.Scale.ScaleY=1;
        }
        _active=-999;_lastStarted=-999;_lastProgress=-1;_lastScale=1;
    }
}
