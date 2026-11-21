using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CharliVision;

// A preview using THE SAME KaraokeWords renderer. No second Karaoke engine.
public sealed class AlignmentPreviewWindow : Window
{
    private readonly LocalAlignmentService.Preview _result;
    private readonly MediaPlayer _player=new();
    private readonly DispatcherTimer _tick=new(){Interval=TimeSpan.FromMilliseconds(33)};
    private readonly KaraokeWords _karaoke;
    private readonly WrapPanel _words=new(){HorizontalAlignment=HorizontalAlignment.Center,
        VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _status=new();
    private readonly TextBlock _fallback=new();
    private readonly Slider _seek=new(){Minimum=0,Height=22,Margin=new Thickness(18,0,18,0)};
    private readonly Button _play=new(){Content="▶ Start",MinWidth=95,Height=33};
    private readonly IReadOnlyList<LyricLine> _lines;
    private bool _open,_dragging,_playback;
    private int _lastLine=int.MinValue;
    public AlignmentPreviewWindow(LocalAlignmentService.Preview result,AlbumPalette palette)
    {
        _result=result;
        _lines=result.ToLyricLines();
        Width=780;Height=370;
        Title="CHARLI VISION — Lokale Karaoke-Vorschau";
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(10,22,29));
        Foreground=Brushes.White;
        MinWidth=580;MinHeight=320;
        _karaoke=new KaraokeWords(_words){GlowStrength=.77,SizeScale=.84};
        _karaoke.ApplyTheme(palette);
        _status.FontSize=12;_status.Foreground=Brushes.LightSkyBlue;
        _status.TextAlignment=TextAlignment.Center;
        _status.Margin=new Thickness(12,8,12,5);
        _status.Text=$"Echte Audioausrichtung · {result.MatchedWords}/{result.SourceWords} zugeordnet · "+
            $"{result.UncertainLines.Count} unsichere Zeilen";
        _fallback.FontSize=26;
        _fallback.FontWeight=FontWeights.SemiBold;
        _fallback.Foreground=Brushes.WhiteSmoke;
        _fallback.TextAlignment=TextAlignment.Center;
        _fallback.TextWrapping=TextWrapping.Wrap;
        _fallback.Visibility=Visibility.Collapsed;
        var center=new Grid{Margin=new Thickness(18,8,18,10)};
        center.Children.Add(_words);
        center.Children.Add(_fallback);
        var playerBar=new StackPanel{Orientation=Orientation.Horizontal,
            HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(12,0,12,15)};
        _play.Click+=(_,_)=>{
            if(!_open){_status.Text="Audio konnte nicht geöffnet werden. Mit dem Regler prüfen.";return;}
            if(_playback){_player.Pause();_playback=false;}
            else{_player.Play();_playback=true;}
            _play.Content=_playback?"Ⅱ Pause":"▶ Start";
        };
        _play.Background=new SolidColorBrush(Color.FromRgb(31,94,107));
        _play.Foreground=Brushes.White;
        playerBar.Children.Add(_play);
        var help=new TextBlock{Text="   Position verschieben = wortgenaue Vorschau",
            Foreground=Brushes.LightGray,VerticalAlignment=VerticalAlignment.Center,
            FontSize=11};
        playerBar.Children.Add(help);
        var layout=new Grid();
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Grid.SetRow(_status,0);layout.Children.Add(_status);
        Grid.SetRow(center,1);layout.Children.Add(center);
        Grid.SetRow(_seek,2);layout.Children.Add(_seek);
        Grid.SetRow(playerBar,3);layout.Children.Add(playerBar);
        Content=layout;
        _seek.Maximum=Math.Max(500,result.DurationMs);
        _seek.PreviewMouseDown+=(_,_)=>_dragging=true;
        _seek.PreviewMouseUp+=(_,_)=>{
            _dragging=false;
            if(_open)_player.Position=TimeSpan.FromMilliseconds(_seek.Value);
            RenderAt(TimeSpan.FromMilliseconds(_seek.Value));
        };
        _seek.ValueChanged+=(_,_)=>{
            if(_dragging || !_playback)
                RenderAt(TimeSpan.FromMilliseconds(_seek.Value));
        };
        _player.MediaOpened+=(_,_)=>{_open=true;_seek.Maximum=
            Math.Max(result.DurationMs,_player.NaturalDuration.HasTimeSpan?
                _player.NaturalDuration.TimeSpan.TotalMilliseconds:0);
            _status.Text=$"Vorschau bereit · Wortzeiten aus CPU-Alignment · "+
                $"{result.UncertainLines.Count} Zeilen zur Prüfung";
        };
        _player.MediaFailed+=(_,e)=>{
            _open=false;
            _status.Text="Audio-Vorschau fehlgeschlagen: "+
                (e.ErrorException?.Message??"Unbekannt");
        };
        _tick.Tick+=(_,_)=>{
            if(_playback && _open){
                var pos=_player.Position;
                if(!_dragging)_seek.Value=pos.TotalMilliseconds;
                RenderAt(pos);
                if(pos.TotalMilliseconds>=_seek.Maximum-70){
                    _player.Pause();_playback=false;_play.Content="▶ Start";
                }
            }
        };
        Loaded+=(_,_)=>{
            try{_player.Open(new Uri(result.AudioPath,UriKind.Absolute));}
            catch(Exception ex){_status.Text="Audio-Vorschau nicht verfügbar: "+ex.Message;}
            _tick.Start();RenderAt(TimeSpan.FromMilliseconds(0));
        };
        Closed+=(_,_)=>{_tick.Stop();_player.Close();};
    }
    internal int CurrentWordIndex=>_karaoke.CurrentWordIndex;
    internal void RenderAt(TimeSpan position)
    {
        int index=LyricsService.ActiveAt(_lines,position);
        if(index<0){
            _words.Children.Clear();
            _fallback.Visibility=Visibility.Collapsed;
            _lastLine=int.MinValue;
            return;
        }
        if(index!=_lastLine){
            _lastLine=index;
            _karaoke.Clear();
            var active=_lines[index];
            if(active.Words is {Count:>0} && _karaoke.Setup(active)){
                _fallback.Visibility=Visibility.Collapsed;
                _words.Visibility=Visibility.Visible;
            }else{
                _words.Visibility=Visibility.Collapsed;
                _fallback.Text=active.Text;
                _fallback.Visibility=Visibility.Visible;
            }
        }
        if(_lines[index].Words is {Count:>0}){
            _words.Visibility=Visibility.Visible;
            _karaoke.Update(position);
        }
    }
}
