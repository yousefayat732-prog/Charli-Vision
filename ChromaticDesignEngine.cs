using System;
using System.Windows.Media;

namespace CharliVision;

/// <summary>
/// Semantic chromatic design tokens. All controls, visualizer elements and lyrics
/// resolve their colors from the same album palette and the same transition clock.
/// </summary>
public readonly record struct ChromaticTokens(
    AlbumPalette Palette,
    Color Accent,
    Color Icon,
    Color TextPrimary,
    Color TextSecondary,
    Color TextActive,
    Color MicroText,
    Color GlassBorder,
    Color GlassGlow,
    Color GlassSurface,
    Color HoverSurface,
    Color KaraokeHighlight)
{
    private static readonly Color Canvas=Color.FromRgb(18,29,39);
    private static double Linear(byte channel)
    {
        double s=channel/255d;
        return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);
    }
    private static double Luminance(Color c)=>
        .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
    public static double Contrast(Color a,Color b)
    {
        double first=Luminance(a),second=Luminance(b);
        return (Math.Max(first,second)+.05)/(Math.Min(first,second)+.05);
    }
    public static Color Readable(Color proposed,double minimum=4.5)
    {
        // The lower-bound contrast is against our dark tinted glass surface.
        if(Contrast(proposed,Canvas)>=minimum)return proposed;
        var white=Colors.White;
        for(int step=1;step<=32;step++){
            var candidate=AlbumPalette.Mix(proposed,white,step/32d);
            if(Contrast(candidate,Canvas)>=minimum)return candidate;
        }
        return white;
    }
    public static ChromaticTokens From(AlbumPalette palette)
    {
        var a=palette.Accent;
        var icon=Readable(AlbumPalette.Mix(palette.Highlight,Colors.White,.27),5);
        var primary=Readable(AlbumPalette.Mix(
            Color.FromRgb(248,250,253),palette.Highlight,.20),7);
        var secondary=Readable(AlbumPalette.Mix(
            Color.FromRgb(178,194,207),palette.Secondary,.20),4.5);
        var active=Readable(AlbumPalette.Mix(a,palette.Highlight,.48),4.5);
        var micro=Readable(AlbumPalette.Mix(
            Color.FromRgb(169,189,201),palette.Secondary,.14),4.5);
        return new ChromaticTokens(
            palette,a,icon,primary,secondary,active,micro,
            Color.FromArgb(130,a.R,a.G,a.B),
            Color.FromArgb(90,a.R,a.G,a.B),
            Color.FromArgb(223,
                (byte)(16+a.R/22),(byte)(23+a.G/24),(byte)(31+a.B/24)),
            Color.FromArgb(50,a.R,a.G,a.B),
            Readable(AlbumPalette.Mix(a,palette.Highlight,.53),4.5)
        );
    }
}

/// <summary>
/// One interpolation clock, one notification stream and at most 25 palette
/// material updates per track change. Nothing polls the album colors from
/// independent per-window timers. Uses elapsed time (not counted frames).
/// </summary>
public sealed class ChromaticDesignEngine
{
    private const int Steps=24;
    private const double Seconds=.72;
    private AlbumPalette _from=AlbumPalette.Neutral;
    private AlbumPalette _target=AlbumPalette.Neutral;
    private AlbumPalette _current=AlbumPalette.Neutral;
    private double _elapsed=Seconds;
    private int _lastStep=Steps;
    public ChromaticTokens Tokens {get;private set;}=
        ChromaticTokens.From(AlbumPalette.Neutral);
    public event Action<ChromaticTokens>? Changed;
    public bool Transitioning=>_elapsed<Seconds;
    public int UpdatesEmitted {get;private set;}
    public void SetTarget(AlbumPalette target,bool immediate=false)
    {
        if(target.Equals(_target)&&!immediate)return;
        _from=_current;
        _target=target;
        _elapsed=immediate?Seconds:0;
        _lastStep=-1;
        if(immediate)Publish(Steps);
    }
    public bool Tick(double elapsedSeconds,bool reducedMotion=false)
    {
        if(_lastStep==Steps && !Transitioning)return false;
        _elapsed=Math.Min(Seconds,_elapsed+
            (reducedMotion?Seconds:Math.Clamp(elapsedSeconds,0,Seconds)));
        int step=Math.Clamp((int)Math.Floor(_elapsed/Seconds*Steps),0,Steps);
        if(step==_lastStep)return false;
        Publish(step);
        return true;
    }
    private void Publish(int step)
    {
        _lastStep=step;
        double t=step/(double)Steps;
        t=t*t*(3-2*t); // one shared smooth-step easing curve
        _current=AlbumPalette.Lerp(_from,_target,t);
        Tokens=ChromaticTokens.From(_current);
        UpdatesEmitted++;
        Changed?.Invoke(Tokens);
    }
}
