using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CharliVision;

/// <summary>
/// Lightweight local-only timed lyrics correction tool.
/// Built-in TextBox Undo/Redo; never captures/downloads Spotify audio.
/// </summary>
public sealed class LyricsEditorWindow : Window
{
    private readonly MainWindow _host;
    private readonly string _songKey,_original;
    private readonly TextBox _source;
    private readonly TextBlock _status=new(){
        FontSize=12,Foreground=Brushes.LightSkyBlue,
        TextWrapping=TextWrapping.Wrap,Margin=new Thickness(14,4,14,10)
    };
    private readonly ComboBox _format=new(){Width=135,Margin=new Thickness(7,0,10,0)};
    private readonly Slider _seek=new(){Minimum=0,Maximum=1000,Margin=new Thickness(10,0,10,0)};
    private readonly WrapPanel _wordPanel=new(){
        HorizontalAlignment=HorizontalAlignment.Center,
        VerticalAlignment=VerticalAlignment.Center
    };
    private readonly TextBlock _linePreview=new(){
        Foreground=Brushes.WhiteSmoke,FontSize=25,
        FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,
        TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center
    };
    private readonly KaraokeWords _karaoke;
    private IReadOnlyList<LyricLine> _previewLines=Array.Empty<LyricLine>();
    private string _mode;
    private int _previewIndex=-999;
    public LyricsEditorWindow(MainWindow host,string songKey,LyricResult result)
    {
        _host=host;_songKey=songKey;
        _mode=result.Quality=="Word"?LyricsEditorDocument.Word:
            result.Quality=="Line"?LyricsEditorDocument.Line:LyricsEditorDocument.Plain;
        _original=LyricsEditorDocument.Export(result);
        Title="CHARLI VISION — Lyrics-Korrektureditor";
        Width=865;Height=695;MinWidth=680;MinHeight=530;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;
        try {
            Icon=System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/CharliVision.ico",
                    UriKind.Absolute));
        }catch{} // A missing resource must not prevent local edits.
        Background=new SolidColorBrush(Color.FromRgb(12,23,32));
        Foreground=Brushes.WhiteSmoke;
        var main=new Grid{
            Margin=new Thickness(18),
            Background=new SolidColorBrush(Color.FromRgb(12,23,32))
        };
        foreach(var height in new[]{
            GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),
            GridLength.Auto,GridLength.Auto,GridLength.Auto
        })main.RowDefinitions.Add(new RowDefinition{Height=height});
        var top=new StackPanel{Margin=new Thickness(0,0,0,13)};
        top.Children.Add(new TextBlock{
            Text="LYRICS & ZEITMARKEN KORRIGIEREN",FontWeight=FontWeights.Bold,
            FontSize=19,Foreground=Brushes.White
        });
        top.Children.Add(new TextBlock{
            Text="Bearbeite deine lokale Kopie. YAML: start_ms/end_ms = Millisekunden. "+
                "LRC: [mm:ss.xx]. Fehlende Wortenden werden nicht erfunden.",
            FontSize=12,TextWrapping=TextWrapping.Wrap,
            Foreground=Brushes.LightGray,Margin=new Thickness(0,6,0,0)
        });
        Grid.SetRow(top,0);main.Children.Add(top);
        var upper=new WrapPanel{Margin=new Thickness(0,0,0,9)};
        upper.Children.Add(new TextBlock{
            Text="Format:",VerticalAlignment=VerticalAlignment.Center
        });
        foreach(string kind in new[]{LyricsEditorDocument.Word,
            LyricsEditorDocument.Line,LyricsEditorDocument.Plain})
            _format.Items.Add(kind);
        _format.SelectedItem=_mode;
        _format.SelectionChanged+=(_,_)=>{
            if(_format.SelectedItem is string kind){
                _mode=kind;_status.Text="Format "+kind+
                    " ausgewählt. Beim Speichern wird die gesamte Datei geprüft.";
            }
        };
        upper.Children.Add(_format);
        upper.Children.Add(Action("−50 ms",()=>Shift(-50)));
        upper.Children.Add(Action("+50 ms",()=>Shift(+50)));
        upper.Children.Add(Action("Rückgängig",()=>{if(_source is {CanUndo:true})_source.Undo();}));
        upper.Children.Add(Action("Wiederholen",()=>{if(_source is {CanRedo:true})_source.Redo();}));
        Grid.SetRow(upper,1);main.Children.Add(upper);
        _source=new TextBox{
            Text=_original,
            AcceptsReturn=true,AcceptsTab=false,
            FontFamily=new FontFamily("Cascadia Code,Consolas"),
            FontSize=13,Padding=new Thickness(11),
            IsUndoEnabled=true,UndoLimit=100,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            TextWrapping=TextWrapping.NoWrap,
            Foreground=Brushes.WhiteSmoke,CaretBrush=Brushes.White,
            Background=new SolidColorBrush(Color.FromRgb(18,33,44)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(58,100,117)),
            BorderThickness=new Thickness(1)
        };
        Grid.SetRow(_source,2);main.Children.Add(_source);
        _karaoke=new KaraokeWords(_wordPanel){SizeScale=.70,GlowStrength=.48};
        _karaoke.ApplyTheme(host.ActivePalette);
        var preview=new Grid{
            Height=90,Margin=new Thickness(5,12,5,4)
        };
        preview.Children.Add(_linePreview);
        preview.Children.Add(_wordPanel);
        _wordPanel.Visibility=Visibility.Collapsed;
        Grid.SetRow(preview,3);main.Children.Add(preview);
        _seek.ValueChanged+=(_,_)=>RenderPreview();
        Grid.SetRow(_seek,4);main.Children.Add(_seek);
        var bottom=new StackPanel();
        var actions=new WrapPanel{Margin=new Thickness(0,10,0,5)};
        actions.Children.Add(Action("Vorschau aktualisieren",LoadPreview));
        actions.Children.Add(Action("Datei importieren",ImportFile));
        actions.Children.Add(Action("Exportieren",ExportFile));
        actions.Children.Add(Action("Original anzeigen",()=>{
            _source.Text=_original;
            _status.Text="Ursprünglich geladener Text wiederhergestellt (noch nicht gespeichert).";
        }));
        actions.Children.Add(Action("Online-Original laden",RestoreFromOnline));
        var save=Action("Änderungen lokal speichern",Save);
        save.Background=new SolidColorBrush(Color.FromRgb(0,118,138));
        actions.Children.Add(save);
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom,5);main.Children.Add(bottom);
        Content=main;
        _status.Text=_original.Length==0?
            "Keine Lyrics geladen. Du kannst eigene LRC-, YAML- oder TXT-Dateien importieren.":
            "Eine lokale Bearbeitung überschreibt keine Online-Quelle.";
        Loaded+=(_,_)=>LoadPreview();
    }
    private static Button Action(string text,System.Action call)
    {
        var b=new Button{
            Content=text,Padding=new Thickness(10,6,10,6),
            Margin=new Thickness(0,0,7,6),
            Background=new SolidColorBrush(Color.FromRgb(35,61,74)),
            Foreground=Brushes.WhiteSmoke,
            BorderBrush=new SolidColorBrush(Color.FromRgb(57,100,112))
        };
        b.Click+=(_,_)=>call();return b;
    }
    private void Shift(int amount)
    {
        string text=LyricsEditorDocument.ShiftCurrentTimestamp(
            _source.Text,_source.CaretIndex,amount);
        if(text==_source.Text){
            _status.Text="Cursor auf die gewünschte Zeitmarken-Zeile setzen.";
            return;
        }
        int caret=_source.CaretIndex;
        _source.SelectAll();
        _source.SelectedText=text;
        _source.CaretIndex=Math.Clamp(caret,0,text.Length);
        _status.Text=$"Zeitmarke {amount:+0;-0} ms verschoben. Vor Speichern prüfen.";
    }
    private void LoadPreview()
    {
        try{
            var result=LyricsEditorDocument.Validate(_source.Text,_mode);
            _previewLines=result.Lines;
            _previewIndex=-999;
            _karaoke.Clear();
            if(result.Quality==LyricsEditorDocument.Plain){
                _seek.Maximum=1000;_seek.Value=0;
                _linePreview.Text=string.Join(" ",
                    (result.PlainText??"").Split('\n').Take(2));
                _linePreview.Visibility=Visibility.Visible;
            }else{
                _seek.Maximum=Math.Max(1000,
                    result.Lines[^1].Time.TotalMilliseconds+3500);
                _seek.Value=Math.Clamp(result.Lines[0].Time.TotalMilliseconds,
                    0,_seek.Maximum);
                RenderPreview();
            }
            _status.Text="Vorschau nutzt die ursprüngliche WPF-Karaoke-Engine.";
        }catch(Exception ex){
            _status.Text="Vorschau: "+ex.Message;
        }
    }
    private void RenderPreview()
    {
        if(_previewLines.Count==0)return;
        var time=TimeSpan.FromMilliseconds(_seek.Value);
        int index=LyricsService.ActiveAt(_previewLines,time);
        if(index<0)return;
        if(index!=_previewIndex){
            _previewIndex=index;
            var line=_previewLines[index];
            if(line.Words is {Count:>0} && _karaoke.Setup(line)){
                _linePreview.Visibility=Visibility.Collapsed;
            }else{
                _karaoke.Clear();
                _linePreview.Text=line.Text;
                _linePreview.Visibility=Visibility.Visible;
            }
        }
        if(_previewLines[index].Words is {Count:>0})
            _karaoke.Update(time);
    }
    private void ImportFile()
    {
        var dialog=new OpenFileDialog{
            Title="Eigene Lyrics-Datei öffnen",
            Filter="Lyrics (LRC, TXT, YAML)|*.lrc;*.txt;*.yaml;*.yml",
            CheckFileExists=true
        };
        if(dialog.ShowDialog(this)!=true)return;
        var file=new FileInfo(dialog.FileName);
        if(file.Length<=0||file.Length>250_000){
            _status.Text="Datei zu groß oder leer.";return;
        }
        string ext=file.Extension.ToLowerInvariant();
        _mode=ext is ".yaml" or ".yml"?LyricsEditorDocument.Word:
            ext==".lrc"?LyricsEditorDocument.Line:LyricsEditorDocument.Plain;
        _format.SelectedItem=_mode;
        _source.Text=File.ReadAllText(dialog.FileName);
        _status.Text="Import geladen; bitte vor dem Speichern kontrollieren.";
        LoadPreview();
    }
    private void ExportFile()
    {
        var suffix=_mode==LyricsEditorDocument.Word?".yaml":
            _mode==LyricsEditorDocument.Line?".lrc":".txt";
        var dialog=new SaveFileDialog{
            Title="Eigene Änderungen exportieren",
            FileName="CharliVision-Bearbeitet"+suffix,
            Filter="Lyrics-Datei|*"+suffix,
            AddExtension=true
        };
        if(dialog.ShowDialog(this)!=true)return;
        try{
            LyricsEditorDocument.Validate(_source.Text,_mode);
            File.WriteAllText(dialog.FileName,_source.Text,
                new System.Text.UTF8Encoding(false));
            _status.Text="Eigene Lyrics-Datei exportiert.";
        }catch(Exception ex){_status.Text="Export fehlgeschlagen: "+ex.Message;}
    }
    private void Save()
    {
        try{
            LyricsEditorDocument.Validate(_source.Text,_mode);
            if(!_host.SaveEditedLyrics(_songKey,_source.Text,_mode)){
                _status.Text="Speichern fehlgeschlagen; Songwechsel oder Dateifehler.";
                return;
            }
            _status.Text="Lokal gespeichert. Das aktuelle Overlay lädt die Korrektur.";
        }catch(Exception ex){_status.Text="Prüfung fehlgeschlagen: "+ex.Message;}
    }
    private void RestoreFromOnline()
    {
        if(MessageBox.Show(this,
            "Alle lokalen Lyrics-Korrekturen für diesen Song löschen "+
            "(zuvor wird eine Sicherung gespeichert) und online neu suchen?",
            "Online-Original wiederherstellen",MessageBoxButton.YesNo,
            MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        if(_host.RestoreOnlineLyrics(_songKey)){
            _status.Text="Lokale Änderungen gesichert; Online-Lyrics werden neu geladen.";
            Close();
        }else _status.Text="Wiederherstellung nicht möglich – möglicherweise Songwechsel.";
    }
}
