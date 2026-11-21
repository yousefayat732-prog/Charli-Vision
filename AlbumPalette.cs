using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;

namespace CharliVision;

public readonly record struct AlbumPalette(Color Accent, Color Secondary, Color Highlight, bool Monochrome)
{
    public static AlbumPalette Neutral => new(
        Color.FromRgb(186,204,220),Color.FromRgb(141,162,180),
        Color.FromRgb(246,249,253),true);
    public static AlbumPalette Default => new(
        Color.FromRgb(245,108,38), Color.FromRgb(249,151,66), Color.FromRgb(255,236,219), false);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t,0,1);
        byte lerp(byte x,byte y) => (byte)Math.Clamp(Math.Round(x+(y-x)*t),0,255);
        return Color.FromArgb(lerp(a.A,b.A),lerp(a.R,b.R),lerp(a.G,b.G),lerp(a.B,b.B));
    }

    public static AlbumPalette Lerp(AlbumPalette a, AlbumPalette b, double t) =>
        new(Mix(a.Accent,b.Accent,t),Mix(a.Secondary,b.Secondary,t),
            Mix(a.Highlight,b.Highlight,t),t>=.5?b.Monochrome:a.Monochrome);

    // Call from a worker, ONLY on frozen album-art BitmapSources.
    public static AlbumPalette Analyze(BitmapSource? cover)
    {
        if (cover is null) return Default;
        try {
            if (!cover.IsFrozen) return Default;
            BitmapSource source = cover;
            // Downsample BEFORE pixel analysis. No full-size texture copy.
            if (cover.PixelWidth > 96 || cover.PixelHeight > 96) {
                var factor=Math.Min(96d/cover.PixelWidth,96d/cover.PixelHeight);
                var scaled=new TransformedBitmap(cover,new ScaleTransform(factor,factor));
                scaled.Freeze();
                source=scaled;
            }
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32) {
                var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
                converted.Freeze();
                source=converted;
            }
            int width=source.PixelWidth, height=source.PixelHeight;
            var pixels=new byte[checked(width*height*4)];
            source.CopyPixels(pixels,width*4,0);
            var buckets=new double[24];
            var sumH=new double[24];
            var sumS=new double[24];
            var sumV=new double[24];
            double grayVotes=0, chromaVotes=0;
            for(int i=0;i<pixels.Length;i+=4) {
                double b=pixels[i]/255d,g=pixels[i+1]/255d,r=pixels[i+2]/255d;
                if(pixels[i+3] < 80) continue;
                var max=Math.Max(r,Math.Max(g,b));
                var min=Math.Min(r,Math.Min(g,b));
                var chroma=max-min;
                double sat=max<=.001?0:chroma/max;
                if(max<.075 || max>.975 && sat<.08) continue;
                if(sat<.20){grayVotes++;continue;}
                chromaVotes++;
                double sampleHue;
                if(chroma<.0001) sampleHue=0;
                else if(max==r) sampleHue=(g-b)/chroma;
                else if(max==g) sampleHue=2+(b-r)/chroma;
                else sampleHue=4+(r-g)/chroma;
                sampleHue=(sampleHue*60+360)%360;
                int bucket=Math.Clamp((int)(sampleHue/15),0,23);
                double weight = (.3+sat*sat*1.7) * Math.Sqrt(Math.Max(.06,max));
                buckets[bucket]+=weight;sumH[bucket]+=sampleHue*weight;
                sumS[bucket]+=sat*weight;sumV[bucket]+=max*weight;
            }
            // Intentional neutral scheme for grayscale covers.
            if(chromaVotes<Math.Max(8,grayVotes*.12)) {
                return new AlbumPalette(Color.FromRgb(204,212,221),
                    Color.FromRgb(153,166,185),Color.FromRgb(250,250,250),true);
            }
            int best=0;
            for(int i=1;i<24;i++)
                if(buckets[i]>buckets[best]) best=i;
            // Look at neighboring hue buckets to avoid abrupt quantization.
            int next=(best+1)%24, prev=(best+23)%24;
            double hWeight=buckets[best], sumHue=sumH[best];
            double totalSat=sumS[best],totalV=sumV[best];
            foreach(int i in new[]{next,prev}){
                if(buckets[i]<buckets[best]*.18)continue;
                hWeight+=buckets[i];totalSat+=sumS[i];totalV+=sumV[i];
                // Wrap hues next to 360 degrees close to 0.
                double normalized=sumH[i]/Math.Max(.001,buckets[i]);
                var main=sumH[best]/Math.Max(.001,buckets[best]);
                if(normalized-main>180)normalized-=360;
                if(main-normalized>180)normalized+=360;
                sumHue+=normalized*buckets[i];
            }
            double hue=(sumHue/Math.Max(.001,hWeight)+360)%360;
            double saturation=Math.Clamp(totalSat/hWeight,.42,.82);
            double brightness=Math.Clamp(totalV/hWeight,.63,.93);
            var accent=FromHsv(hue,saturation,brightness);
            int second=-1;
            for(int i=0;i<24;i++){
                double delta=Math.Abs(i*15-hue);delta=Math.Min(delta,360-delta);
                if(delta>24 && delta<105 && (second<0 || buckets[i]>buckets[second]))second=i;
            }
            double hue2=second>=0&&buckets[second]>.15*buckets[best] ? (sumH[second]/buckets[second]) : (hue+27)%360;
            var secondary=FromHsv(hue2,Math.Clamp(saturation*.80,.3,.70),.92);
            var highlight=Mix(accent,Color.FromRgb(255,255,255),.68);
            return new AlbumPalette(accent,secondary,highlight,false);
        } catch(Exception ex) {
            SettingsStore.Log(ex);
            return Default;
        }
    }
    private static Color FromHsv(double hue,double saturation,double value)
    {
        hue=(hue+360)%360;
        double chroma=value*saturation;
        double x=chroma*(1-Math.Abs((hue/60)%2-1));
        double m=value-chroma;
        (double r,double g,double b)=(hue/60) switch {
            <1 => (chroma,x,0d),
            <2 => (x,chroma,0d),
            <3 => (0d,chroma,x),
            <4 => (0d,x,chroma),
            <5 => (x,0d,chroma),
            _  => (chroma,0d,x)
        };
        return Color.FromRgb((byte)((r+m)*255),(byte)((g+m)*255),(byte)((b+m)*255));
    }
}