using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharliVision;

public enum VinylQuality { UltraLight, Balanced, Cinematic }

/// <summary>
/// Fully native WPF vinyl + 92-band real audio ring. Static microgrooves are a
/// frozen DrawingGroup, so the record does not rebuild hundreds of shapes/frame.
/// </summary>
public sealed class VinylVisual : FrameworkElement
{
    private const int Bars=92,PaletteSteps=24;
    private const double RecordRadius=139,SpectrumRadius=151.5;
    // 350px viewport radius=175; even maximum rounded glow must fit inside.
    private const double SpectrumMaximumGlowRadius=4.65;
    internal static double SpectrumTipRadius(double normalized)=>
        SpectrumRadius+3.4+14.45*Math.Pow(Math.Clamp(normalized,0,1),.83);
    internal static double SpectrumTipWithGlow=>SpectrumTipRadius(1)+SpectrumMaximumGlowRadius;
    internal static (double X,double Y) SpectrumPoint(int bar,double normalized){
        double angle=2*Math.PI*bar/Bars-Math.PI/2;
        double r=SpectrumTipRadius(normalized);
        return (r*Math.Cos(angle),r*Math.Sin(angle));
    }
    private readonly float[] _display=new float[Bars];
    private readonly double[] _cos=new double[Bars],_sin=new double[Bars];
    private readonly Pen[] _corePens=new Pen[16],_glowPens=new Pen[16];
    private readonly Pen _centerNeedle=PenFor(Color.FromArgb(112,220,224,228),.55);
    private static readonly Geometry _discCutout=MakeDiscCutout();
    private static readonly Brush _darkSpindle=Solid(Color.FromRgb(17,19,23));
    private float[]? _audio;
    private double _angle,_speed,_coverFade=1,_paletteFade=1,_bassEnergy;
    private bool _playing;
    // Cover art is clipped/rasterized once per song, never cropped per frame.
    // The frozen textures rotate at exactly the same angle as the grooves.
    private BitmapSource? _cover,_previousCover;
    private BitmapSource? _coverTexture,_previousCoverTexture;
    private const int CoverTexturePixels=512;
    public bool CachedCoverFrozen=>_coverTexture is null || _coverTexture.IsFrozen;
    public int CachedCoverPixelWidth=>_coverTexture?.PixelWidth??0;
    private AlbumPalette _from=AlbumPalette.Default,_target=AlbumPalette.Default,
        _current=AlbumPalette.Default;
    private int _lastPaletteStep=-1;
    private DrawingGroup _grooves=new();
    private BitmapSource? _grooveTexture;
    private Brush _body=Brushes.Black,_reflection=Brushes.Transparent,
        _ambientHalo=Brushes.Transparent,_coverFallback=Brushes.Black,
        _coverVignette=Brushes.Transparent,_arcLight=Brushes.Transparent;
    private Pen _outerEdge=PenFor(Colors.Gray,1),
        _labelEdge=PenFor(Colors.Gray,1),
        _labelShadow=PenFor(Colors.Black,2),
        _innerSheen=PenFor(Colors.Gray,1),
        _spectrumGuide=PenFor(Colors.Transparent,1);
    private readonly Geometry _coverClip=new EllipseGeometry(new Point(0,0),63.1,63.1);
    private readonly RotateTransform _rotateRecord=new();
    private readonly RotateTransform _rotateCover=new();
    private readonly TranslateTransform _centerShift=new();
    private readonly ScaleTransform _drawingScale=new();
    private readonly Geometry _arc1=MakeArc(75,136,-65,-5);
    private readonly Geometry _arc2=MakeArc(84,135,118,166);
    private VinylQuality _quality=VinylQuality.Balanced;
    public AlbumPalette CurrentPalette=>_current;
    public VinylQuality Quality=>_quality;
    public bool CoverRotates {get;set;}=true;
    private bool _centerTransparent=true;
    public bool CenterTransparent {
        get=>_centerTransparent;
        set {if(_centerTransparent!=value){_centerTransparent=value;InvalidateVisual();}}
    }
    public bool RotationEnabled {get;set;}=true;
    // Uses the standard 33⅓ RPM unless the optional local tempo estimator
    // has accumulated enough stable, real audio onsets.
    public double TargetRotationRpm {get;set;}=33.333;
    public double SmoothingFactor {get;set;}=1;
    public bool CachedMaterialFrozen=>_grooves.IsFrozen && _grooveTexture?.IsFrozen==true;
    public double RotationDegrees=>_angle;
    public double RotationSpeed=>_speed;
    public double LastRenderMs{get;private set;}
    public int GrooveCount{get;private set;}
    public float SpectrumValue(int index)=>index>=0 && index<Bars?_display[index]:0;

    public VinylVisual()
    {
        for(int i=0;i<Bars;i++){
            double radians=2*Math.PI*i/Bars-Math.PI/2;
            _cos[i]=Math.Cos(radians);_sin[i]=Math.Sin(radians);
        }
        RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);
        BuildGrooves();
        UpdateResources(_current);
        Cursor=System.Windows.Input.Cursors.Hand;
        IsHitTestVisible=true;
    }
    private static SolidColorBrush Solid(Color color)
    {
        var b=new SolidColorBrush(color);b.Freeze();return b;
    }
    private static Pen PenFor(Color color,double width)
    {
        var pen=new Pen(Solid(color),width){
            StartLineCap=PenLineCap.Round,
            EndLineCap=PenLineCap.Round,
            LineJoin=PenLineJoin.Round
        };
        pen.Freeze();return pen;
    }
    private static Brush Radial(params GradientStop[] stops)
    {
        var b=new RadialGradientBrush(new GradientStopCollection(stops)){
            Center=new Point(.5,.5),GradientOrigin=new Point(.38,.31),
            RadiusX=.65,RadiusY=.68
        };
        b.Freeze();return b;
    }
    private static Geometry MakeArc(double inner,double outer,double a0,double a1)
    {
        Point P(double radius,double degrees){
            double t=degrees*Math.PI/180;
            return new Point(radius*Math.Cos(t),radius*Math.Sin(t));
        }
        var geo=new StreamGeometry();
        using(var ctx=geo.Open()){
            ctx.BeginFigure(P(outer,a0),true,true);
            ctx.ArcTo(P(outer,a1),new Size(outer,outer),0,
                Math.Abs(a1-a0)>180,SweepDirection.Clockwise,true,false);
            ctx.LineTo(P(inner,a1),true,false);
            ctx.ArcTo(P(inner,a0),new Size(inner,inner),0,
                Math.Abs(a1-a0)>180,SweepDirection.Counterclockwise,true,false);
        }
        geo.Freeze();return geo;
    }
    private static Geometry MakeDiscCutout()
    {
        // Geometry exclusion is true alpha, not a black painted circle.
        var donut=new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(0,0),RecordRadius+.5,RecordRadius+.5),
            new EllipseGeometry(new Point(0,0),4,4));
        donut.Freeze();
        return donut;
    }
    private static double Hash01(int i)
    {
        uint x=unchecked((uint)i*747796405u+2891336453u);
        x=(x^(x>>16))*2246822519u;
        x^=x>>13;
        return (x&65535)/65535d;
    }
    private void BuildGrooves()
    {
        var material=new DrawingGroup();
        int count=0;
        using(var ctx=material.Open()){
            double radius=67.1;
            while(radius<137.2){
                int index=count++;
                double n=Hash01(index+7);
                // Natural pressing bands: tiny variations in spacing, alpha, width.
                double step=(_quality==VinylQuality.UltraLight?2.1:.83)
                    +(_quality==VinylQuality.UltraLight?.33:.41)*n;
                radius+=step;
                if(radius>137.6)break;
                if(index%17==9)continue; // minute groove micro-gaps
                bool band=(index%21==5 || index%29==14);
                byte alpha=(byte)(band?28:10+(int)(n*18));
                double width=band?.65:.25+.24*Hash01(index+167);
                Color ink=Color.FromArgb(alpha,
                    (byte)(120+25*n),(byte)(127+19*n),(byte)(136+19*n));
                ctx.DrawEllipse(null,PenFor(ink,width),new Point(0,0),radius,radius);
                if(_quality==VinylQuality.Cinematic && index%12==2){
                    // Incomplete microscopic groove reflection, cached and barely visible.
                    var sheen=MakeArc(radius-.03,radius+.03,
                        12+65*Hash01(index+77),46+65*Hash01(index+77));
                    ctx.DrawGeometry(Solid(Color.FromArgb(19,211,221,239)),null,sheen);
                }
            }
        }
        material.Freeze();
        _grooves=material;
        GrooveCount=count;
        // Rasterize the neutral groove material once. This avoids WPF processing
        // ~70 concentric ellipse drawings on every single animation frame.
        int size=_quality switch {
            VinylQuality.UltraLight=>448,
            VinylQuality.Cinematic=>768,
            _=>640
        };
        var visual=new DrawingVisual();
        using(var paint=visual.RenderOpen()){
            paint.PushTransform(new TranslateTransform(size/2d,size/2d));
            double ratio=size/(2d*RecordRadius);
            paint.PushTransform(new ScaleTransform(ratio,ratio));
            paint.DrawDrawing(material);
            paint.Pop();paint.Pop();
        }
        var cached=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);
        cached.Render(visual);
        cached.Freeze();
        _grooveTexture=cached;
    }
    public void SetQuality(VinylQuality quality)
    {
        if(_quality==quality)return;
        _quality=quality;
        BuildGrooves();
        InvalidateVisual();
    }
    public void SetPalette(AlbumPalette palette)
    {
        if(_target.Equals(palette))return;
        _from=_current;
        _target=palette;_paletteFade=0;_lastPaletteStep=-1;
        InvalidateVisual();
    }
    // Called only by the centralized ChromaticDesignEngine. The vinyl no
    // longer runs a second, competing album-color interpolation clock.
    public void SetPaletteImmediate(AlbumPalette palette)
    {
        if(_current.Equals(palette))return;
        _from=palette;_target=palette;_current=palette;
        _paletteFade=1;_lastPaletteStep=PaletteSteps;
        UpdateResources(palette);
        InvalidateVisual();
    }
    private static BitmapSource? RasterizeCover(BitmapSource? art)
    {
        if(art is null)return null;
        // Preserve the original center-crop and circular alpha, but only
        // perform clipping and image resampling when a song actually changes.
        const int pixels=CoverTexturePixels;
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()){
            dc.PushClip(new EllipseGeometry(new Point(pixels/2d,pixels/2d),
                pixels/2d-.5,pixels/2d-.5));
            double ratio=art.PixelWidth/(double)Math.Max(1,art.PixelHeight);
            double width=ratio>=1?pixels*ratio:pixels;
            double height=ratio>=1?pixels:pixels/ratio;
            dc.DrawImage(art,new Rect((pixels-width)/2d,
                (pixels-height)/2d,width,height));
            dc.Pop();
        }
        var bitmap=new RenderTargetBitmap(pixels,pixels,96,96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
    public void SetTrack(bool playing,BitmapSource? cover,bool changed)
    {
        _playing=playing;
        if(changed){
            _previousCover=_cover;
            _previousCoverTexture=_coverTexture;
            _cover=cover;
            _coverTexture=RasterizeCover(cover);
            _coverFade=0;
        }
        InvalidateVisual();
    }
    public void Tick(double dt,float[]? levels)
    {
        // Integrate the velocity curve over real monotonic elapsed time.
        // Unlike the old 240-ms dt clamp, missed frames never cause lost
        // physical rotation. Analytic integration also avoids frame-size drift.
        dt=double.IsFinite(dt)?Math.Max(0,dt):0;
        double safeRpm=double.IsFinite(TargetRotationRpm)?
            Math.Clamp(TargetRotationRpm,24,52):33.333;
        double targetSpeed=_playing&&RotationEnabled?safeRpm*6:0;
        // A reliable tempo change eases over ~2.6s; the classic 33⅓ RPM
        // remains fast to start and preserves analytic frame independence.
        double speedTau=_playing?
            (Math.Abs(safeRpm-33.333)>.05?2.6:.36):.48;
        double speedBefore=_speed;
        double attenuation=Math.Exp(-dt/speedTau);
        _speed=targetSpeed+(speedBefore-targetSpeed)*attenuation;
        double travelled=targetSpeed*dt+
            (speedBefore-targetSpeed)*speedTau*(1-attenuation);
        _angle=(_angle+travelled)%360;
        if(_speed<.0001)_speed=0;
        _coverFade=Math.Min(1,_coverFade+dt/0.44);
        _paletteFade=Math.Min(1,_paletteFade+dt/.75);
        int step=(int)Math.Round(_paletteFade*PaletteSteps);
        _current=AlbumPalette.Lerp(_from,_target,_paletteFade);
        if(step!=_lastPaletteStep){
            _lastPaletteStep=step;
            UpdateResources(AlbumPalette.Lerp(_from,_target,step/(double)PaletteSteps));
        }
        if(levels?.Length==Bars)_audio=levels;
        for(int i=0;i<Bars;i++){
            double target=0;
            if(_playing && _audio is not null){
                var raw=_audio[i];
                target=float.IsFinite(raw)?Math.Clamp(raw,0,1):0;
                if(target<.005)target=0;
            }
            double previous=_display[i];
            // Exponential attack/release behaves consistently at variable FPS.
            double timeConstant=(target>previous?.036:.165)*
                Math.Clamp(SmoothingFactor,.5,2.5);
            _display[i]=(float)(previous+(target-previous)*
                (1-Math.Exp(-dt/timeConstant)));
            if(_display[i]<.0003)_display[i]=0;
        }
        double bass=0;
        for(int i=0;i<12;i++)bass+=_display[i];
        bass=_playing?Math.Clamp(bass/12,0,1):0;
        _bassEnergy+=(bass-_bassEnergy)*(1-Math.Exp(-dt/(_bassEnergy<bass?.08:.19)));
        if(_coverFade>=1){
            _previousCover=null;
            _previousCoverTexture=null;
        }
        if(_playing || _speed>.025 || _coverFade<1 || _paletteFade<1 ||
            _bassEnergy>.004 || AnySpectrum())
            InvalidateVisual();
    }
    private bool AnySpectrum()
    {
        for(int i=0;i<Bars;i+=4)if(_display[i]>.003)return true;
        return false;
    }
    private void UpdateResources(AlbumPalette p)
    {
        var color=p.Accent;
        for(int i=0;i<16;i++){
            double f=i/15d;
            Color lit=AlbumPalette.Mix(color,p.Highlight,.16+.43*f);
            _corePens[i]=PenFor(Color.FromArgb((byte)(215+39*f),
                lit.R,lit.G,lit.B),3.1+.65*f);
            _glowPens[i]=PenFor(Color.FromArgb((byte)(13+30*f),
                color.R,color.G,color.B),7.8+1.5*f);
        }
        var tinted=AlbumPalette.Mix(Color.FromRgb(14,15,20),color,.09);
        _body=Radial(
            new GradientStop(Color.FromRgb(50,51,59),0),
            new GradientStop(Color.FromRgb(26,27,35),.31),
            new GradientStop(Color.FromRgb(14,15,22),.65),
            new GradientStop(tinted,.88),
            new GradientStop(Color.FromRgb(7,8,12),1));
        _reflection=new LinearGradientBrush(
            new GradientStopCollection{
                new GradientStop(Colors.Transparent,0),
                new GradientStop(Color.FromArgb(32,p.Highlight.R,p.Highlight.G,p.Highlight.B),.21),
                new GradientStop(Colors.Transparent,.42),
                new GradientStop(Color.FromArgb(25,173,190,220),.59),
                new GradientStop(Colors.Transparent,.77),
                new GradientStop(Color.FromArgb(18,240,245,255),1)
            },new Point(.08,.07),new Point(.92,.90));
        _reflection.Freeze();
        _ambientHalo=Radial(
            new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),0),
            new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),.78),
            new GradientStop(Color.FromArgb(19,color.R,color.G,color.B),.96),
            new GradientStop(Color.FromArgb(0,color.R,color.G,color.B),1));
        _outerEdge=PenFor(Color.FromArgb(113,color.R,color.G,color.B),.85);
        _innerSheen=PenFor(Color.FromArgb(58,p.Highlight.R,p.Highlight.G,p.Highlight.B),.7);
        _spectrumGuide=PenFor(Color.FromArgb(67,color.R,color.G,color.B),.92);
        _labelShadow=PenFor(Color.FromArgb(155,0,0,4),5.3);
        _labelEdge=PenFor(Color.FromArgb(131,p.Highlight.R,p.Highlight.G,p.Highlight.B),.9);
        _arcLight=new LinearGradientBrush(
            new GradientStopCollection{
                new GradientStop(Colors.Transparent,0),
                new GradientStop(Color.FromArgb(14,p.Highlight.R,p.Highlight.G,p.Highlight.B),.44),
                new GradientStop(Color.FromArgb(24,p.Highlight.R,p.Highlight.G,p.Highlight.B),.51),
                new GradientStop(Colors.Transparent,1)
            },new Point(0,0),new Point(1,1));
        _arcLight.Freeze();
        _coverVignette=Radial(
            new GradientStop(Colors.Transparent,0),
            new GradientStop(Colors.Transparent,.74),
            new GradientStop(Color.FromArgb(35,1,1,5),.91),
            new GradientStop(Color.FromArgb(87,1,1,4),1));
        _coverFallback=Radial(
            new GradientStop(AlbumPalette.Mix(color,Colors.Black,.70),0),
            new GradientStop(AlbumPalette.Mix(color,Colors.Black,.91),1));
    }
    protected override void OnRender(DrawingContext dc)
    {
        long started=Stopwatch.GetTimestamp();
        base.OnRender(dc);
        double scale=Math.Min(ActualWidth,ActualHeight)/350;
        if(scale<=0)return;
        var center=new Point(ActualWidth*.5,ActualHeight*.5);
        _centerShift.X=center.X;_centerShift.Y=center.Y;
        _drawingScale.ScaleX=scale;_drawingScale.ScaleY=scale;
        dc.PushTransform(_centerShift);
        dc.PushTransform(_drawingScale);
        DrawSpectrum(dc);
        // Light belongs to the scene; the vinyl texture and album label rotate beneath it.
        double haloRadius=RecordRadius+3+1.6*_bassEnergy;
        dc.DrawEllipse(_ambientHalo,null,new Point(0,0),haloRadius,haloRadius);
        // Clip every record layer, including the cover, to an actual
        // annulus so Wallpaper Engine shows through the spindle hole.
        if(CenterTransparent)dc.PushClip(_discCutout);
        dc.DrawEllipse(_body,_outerEdge,new Point(0,0),RecordRadius,RecordRadius);
        _rotateRecord.Angle=_angle;
        dc.PushTransform(_rotateRecord);
        if(_grooveTexture is not null)
            dc.DrawImage(_grooveTexture,new Rect(-RecordRadius,-RecordRadius,
                RecordRadius*2,RecordRadius*2));
        else dc.DrawDrawing(_grooves);
        dc.Pop();
        if(_quality!=VinylQuality.UltraLight){
            dc.DrawEllipse(_reflection,null,new Point(0,0),RecordRadius-1,RecordRadius-1);
            dc.DrawGeometry(_arcLight,null,_arc1);
            if(_quality==VinylQuality.Cinematic)
                dc.DrawGeometry(_arcLight,null,_arc2);
        }
        // Metallic runout and inset label, without a stark empty black ring.
        dc.DrawEllipse(null,_innerSheen,new Point(0,0),67.9,67.9);
        dc.DrawEllipse(null,_labelShadow,new Point(0,0),64.7,64.7);
        if(CoverRotates){
            _rotateCover.Angle=_angle;
            dc.PushTransform(_rotateCover);
        }
        DrawCover(dc,_previousCoverTexture,1-_coverFade);
        DrawCover(dc,_coverTexture,_coverFade);
        if(CoverRotates)dc.Pop();
        dc.DrawEllipse(_coverVignette,_labelEdge,new Point(0,0),63.3,63.3);
        // The subtle edge is drawn outside the hole; no filled dot overlays it.
        dc.DrawEllipse(null,_centerNeedle,new Point(0,0),4.5,4.5);
        if(CenterTransparent)dc.Pop();
        else dc.DrawEllipse(_darkSpindle,_centerNeedle,
            new Point(0,0),3.9,3.9);
        dc.Pop();dc.Pop();
        LastRenderMs=(Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency;
    }
    private void DrawSpectrum(DrawingContext dc)
    {
        dc.DrawEllipse(null,_spectrumGuide,new Point(0,0),
            SpectrumRadius-.8,SpectrumRadius-.8);
        for(int i=0;i<Bars;i++){
            int left=(i+Bars-1)%Bars,right=(i+1)%Bars;
            double value=.64*_display[i]+.18*(_display[left]+_display[right]);
            double raw=Math.Clamp(value,0,1);
            int index=Math.Clamp((int)Math.Round(raw*15),0,15);
            double start=SpectrumRadius+1.2;
            // Previously the 177.7px endpoint exceeded the 175px viewport.
            // Keep max rounded pen/glow fully inside all 360° of the ring.
            double end=SpectrumTipRadius(raw);
            var a=new Point(start*_cos[i],start*_sin[i]);
            var b=new Point(end*_cos[i],end*_sin[i]);
            if(raw>.065 && _quality!=VinylQuality.UltraLight)
                dc.DrawLine(_glowPens[index],a,b);
            dc.DrawLine(_corePens[index],a,b);
        }
    }
    private void DrawCover(DrawingContext dc,BitmapSource? texture,double opacity)
    {
        if(opacity<=.0001)return;
        dc.PushOpacity(opacity);
        if(texture is null)
            dc.DrawEllipse(_coverFallback,null,new Point(0,0),63.1,63.1);
        else
            dc.DrawImage(texture,new Rect(-63.1,-63.1,126.2,126.2));
        dc.Pop();
    }
}