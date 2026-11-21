using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CharliVision;

// Optional one-shot diagnostics, never started during normal app operation.
public static class LyricsCoverageTests
{
    private sealed record Case(string Category,string Title,string Artist,
        string Album,double Seconds);
    private static readonly Case[] Songs={
        new("International","Yellow","Coldplay","Parachutes",0),
        new("Deutsch","Du hast","Rammstein","Sehnsucht",0),
        new("Englisch","Someone Like You","Adele","21",0),
        new("Rap","Lose Yourself","Eminem","8 Mile",0),
        new("Unbekannter","Wicked Game","Chris Isaak","Heart Shaped World",0),
        new("Live","Hotel California (Live)","Eagles","Hell Freezes Over",0),
        new("Remix","Summertime Sadness (Cedric Gervais Remix)","Lana Del Rey","",0),
        new("Worttest","I Want to Live","Borislav Slavov","Baldur's Gate 3 (Original Game Soundtrack)",233),
        new("Zeilentest","Take On Me","a-ha","Hunting High and Low",0),
        new("Plain-only Kandidat","Hello There","The Freshmen","",229),
        new("Abweichende Laufzeit","November","JM7","",190),
        new("Ohne Treffer","Definitely nonexistent original x8931",
            "No Such Artist x8931","",0),
        new("Mehrere Versionen","Yellow","Coldplay","Parachutes",0),
        new("Wiederholung","Yellow","Coldplay","Parachutes",0)
    };
    public static bool Run()
    {
        var rows=new List<string>{
            "CHARLI VISION LYRICS COVERAGE: one-time provider smoke test "+DateTimeOffset.Now.ToString("u"),
            "Anzahl=14, Metadaten-Testauswahl; keine Songtexte protokolliert.",
            "Quellen benötigen Netzwerk und können Limits setzen.",
            "Kategorie | Quelle | Qualität | Treffer | Cache | Requests | ms | Status"
        };
        int word=0,line=0,plain=0,miss=0,cached=0;
        try {
            Task.Run(async()=>{
                using var service=new LyricsService();
                for(int i=0;i<Songs.Length;i++){
                    var song=Songs[i];
                    using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var timer=Stopwatch.StartNew();
                    try {
                        TimeSpan? duration=song.Seconds>1?
                            TimeSpan.FromSeconds(song.Seconds):null;
                        var result=await service.GetAsync(song.Title,song.Artist,
                            song.Album,duration,cts.Token);
                        bool hit=result.Quality!="Keine";
                        if(result.Quality=="Word")word++;
                        else if(result.Quality=="Line")line++;
                        else if(result.Quality=="Plain")plain++;
                        else miss++;
                        if(service.LastCacheHit)cached++;
                        rows.Add($"{song.Category} | {result.Source} | {result.Quality} | "+
                            $"{hit} | {service.LastCacheHit} | "+
                            $"{(service.LastCacheHit?0:service.LastProviderAttempts)} | "+
                            $"{timer.ElapsedMilliseconds} | {result.Status}");
                    }catch(OperationCanceledException){
                        miss++;
                        rows.Add($"{song.Category} | Timeout | Keine | False | False | - | "+
                            $"{timer.ElapsedMilliseconds} | Zeitlimit");
                    }catch(Exception ex){
                        miss++;
                        rows.Add($"{song.Category} | Fehler | Keine | False | False | - | "+
                            $"{timer.ElapsedMilliseconds} | {ex.GetType().Name}");
                    }
                    if(i<Songs.Length-1)await Task.Delay(220);
                }
            }).GetAwaiter().GetResult();
        }catch(Exception ex){rows.Add("Benchmark fatal: "+ex.GetType().Name);}
        rows.Add($"SUMMARY Word={word}; Line={line}; Plain={plain}; None={miss}; CacheHits={cached}; "+
            $"Cases={Songs.Length}");
        try{
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                "lyrics-coverage-test.txt"),rows);
        }catch(Exception ex){SettingsStore.Log(ex);return false;}
        return word+line+plain+miss==Songs.Length;
    }
}