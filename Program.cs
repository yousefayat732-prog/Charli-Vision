using System;
using Velopack;
namespace CharliVision;
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app=new App();
        app.InitializeComponent();
        app.Run();
    }
}
