using System;
using System.Windows.Media;

namespace CharliVision;

/// <summary>Lyrics-only text colors; Auto retains the existing album-color design.</summary>
public static class LyricsTextColorMode
{
    public static string Normalize(string? value)=>value switch{
        "White"=>"White",
        "Black"=>"Black",
        _=>"Auto"
    };
    public static bool IsManual(string? value)=>Normalize(value)!="Auto";
    public static Color Primary(string? value,AlbumPalette palette,bool highContrast)=>
        Normalize(value) switch{
            "Black"=>Colors.Black,
            "White"=>Colors.White,
            _=>highContrast?Colors.White:AlbumPalette.Mix(Colors.White,palette.Highlight,.55)
        };
    public static Color Secondary(string? value,AlbumPalette palette,bool highContrast)=>
        Normalize(value) switch{
            "Black"=>Color.FromRgb(49,49,49),
            "White"=>Color.FromRgb(232,232,232),
            _=>highContrast?Color.FromRgb(224,232,241):
                AlbumPalette.Mix(Color.FromRgb(175,182,192),palette.Secondary,.20)
        };
}
