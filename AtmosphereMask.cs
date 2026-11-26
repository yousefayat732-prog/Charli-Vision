using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharliVision;

/// <summary>Reusable, softly feathered alpha texture. No frame-by-frame blur,
/// no hard ellipse outline, no text effects.</summary>
internal static class AtmosphereMask
{
    internal static ImageBrush Create()
    {
        const int w=256,h=160;
        byte[] pixels=new byte[w*h*4];
        static double Smooth(double v){v=Math.Clamp(v,0,1);return v*v*(3-2*v);}
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            double u=x/(double)(w-1),v=y/(double)(h-1);
            double px=(u-.5)*2,py=(v-.5)*2;
            double core=Math.Exp(-2.5*(px*px+py*py));
            double edge=Smooth(Math.Min(Math.Min(u,1-u),Math.Min(v,1-v))/.22);
            double noise=((x*73+y*151)%17-8)/17d;
            byte a=(byte)Math.Clamp(Math.Round(core*edge*255+noise),0,255);
            if(x==0||y==0||x==w-1||y==h-1)a=0;
            int i=(y*w+x)*4;
            pixels[i]=pixels[i+1]=pixels[i+2]=pixels[i+3]=a;
        }
        var mask=BitmapSource.Create(w,h,96,96,PixelFormats.Pbgra32,null,pixels,w*4);
        mask.Freeze();
        var brush=new ImageBrush(mask){Stretch=Stretch.Fill};
        brush.Freeze();return brush;
    }
}
