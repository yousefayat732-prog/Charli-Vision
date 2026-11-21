using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CharliVision;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    public App()
    {
        DispatcherUnhandledException += (object sender,DispatcherUnhandledExceptionEventArgs e) =>
        {
            SettingsStore.Log(e.Exception);
            MessageBox.Show("Charli Vision hat einen Fehler festgestellt. Details: %APPDATA%\\CharliVision\\errors.log",
                "Charli Vision",MessageBoxButton.OK,MessageBoxImage.Warning);
            e.Handled=true;
        };
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        var frameMode=Array.Find(e.Args,
            x=>x.StartsWith("--vinyl-frame-test",StringComparison.OrdinalIgnoreCase));
        if(frameMode is not null){
            // Explicitly opted-in QA window; normal overlay never starts it.
            // A nested one-shot WPF Dispatcher pump completes before normal
            // StartupUri handling. Never modify the main app's startup URI.
            string profile=frameMode.EndsWith("-light",
                StringComparison.OrdinalIgnoreCase)?"Light":
                frameMode.EndsWith("-balanced",StringComparison.OrdinalIgnoreCase)?
                    "Balanced":frameMode.EndsWith("-smooth",StringComparison.OrdinalIgnoreCase)?
                    "Smooth":frameMode.EndsWith("-high",StringComparison.OrdinalIgnoreCase)?
                    "High":frameMode.EndsWith("-long",StringComparison.OrdinalIgnoreCase)?
                    "Long":frameMode.EndsWith("-native",StringComparison.OrdinalIgnoreCase)?
                    "Native":frameMode.EndsWith("-linear",
                        StringComparison.OrdinalIgnoreCase)?"Linear":"Cinematic";
            FramePacingSmokeTest.RunBlocking(this,profile);
            return;
        }
        if(Array.Exists(e.Args,x=>x.Equals("--lyrics-coverage-test",StringComparison.OrdinalIgnoreCase))){
            bool okay=LyricsCoverageTests.Run();
            if(!okay)Environment.ExitCode=1;
            Shutdown();return;
        }
        if(Array.Exists(e.Args,x=>x.Equals("--spotify-skip-test",
            StringComparison.OrdinalIgnoreCase))) {
            bool okay=SpotifySessionSmokeTest.Run(resume:true,skipAndRestore:true);
            if(!okay)Environment.ExitCode=2;
            Shutdown();return;
        }
        if(Array.Exists(e.Args,x=>x.Equals("--spotify-session-test",
            StringComparison.OrdinalIgnoreCase))) {
            bool okay=SpotifySessionSmokeTest.Run(resume:true,skipAndRestore:false);
            if(!okay)Environment.ExitCode=2;
            Shutdown();return;
        }
        if(Array.Exists(e.Args,x=>x.Equals("--alignment-self-test",
            StringComparison.OrdinalIgnoreCase))) {
            bool okay=LocalAlignmentSmokeTest.Run();
            if(!okay)Environment.ExitCode=1;
            Shutdown();return;
        }
        if(Array.Exists(e.Args,x=>x.Equals("--self-test",StringComparison.OrdinalIgnoreCase))) {
            bool okay=SelfTests.Run();
            if(!okay)Environment.ExitCode=1;
            Shutdown();
            return;
        }
        _instanceMutex = new Mutex(true,@"Local\CharliVisionStandaloneApp",out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }
        base.OnStartup(e);
        // Opt-in visual QA; normal launches never open the settings automatically.
        if(Array.Exists(e.Args,x=>x.StartsWith("--settings-preview",StringComparison.OrdinalIgnoreCase)) ||
           Array.Exists(e.Args,x=>x.Equals("--glass-capture",StringComparison.OrdinalIgnoreCase)) ||
           Array.Exists(e.Args,x=>x.Equals("--glass-capture-sync",StringComparison.OrdinalIgnoreCase)) ||
           Array.Exists(e.Args,x=>x.Equals("--settings-capture",StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                if(MainWindow is MainWindow overlay){
                    if(Array.Exists(e.Args,x=>x.Equals("--settings-preview-keys",
                        StringComparison.OrdinalIgnoreCase)))
                        overlay.OpenAdvancedSettings("Keys");
                    else if(Array.Exists(e.Args,x=>x.Equals("--settings-capture",
                        StringComparison.OrdinalIgnoreCase)))
                        overlay.OpenAdvancedSettings("Playback");
                    else overlay.OpenSettings();
                    if(Array.Exists(e.Args,x=>x.StartsWith("--glass-capture",
                        StringComparison.OrdinalIgnoreCase)) ||
                       Array.Exists(e.Args,x=>x.Equals("--settings-capture",
                        StringComparison.OrdinalIgnoreCase))){
                        var previewTimer=new DispatcherTimer{
                            Interval=TimeSpan.FromMilliseconds(650)};
                        previewTimer.Tick+=(_,_)=>{
                            previewTimer.Stop();
                            try{
                                if(Array.Exists(e.Args,x=>x.Equals("--glass-capture-sync",
                                    StringComparison.OrdinalIgnoreCase)))
                                    overlay.CaptureGlassSyncPreview();
                                else if(Array.Exists(e.Args,x=>x.Equals("--settings-capture",
                                    StringComparison.OrdinalIgnoreCase)))
                                    overlay.CaptureSettingsPreview();
                                else overlay.CaptureGlassPreview();
                            }catch(Exception ex){SettingsStore.Log(ex);}
                        };
                        previewTimer.Start();
                    }
                }
            }));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}