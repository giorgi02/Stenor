using Stenor.Services;
using Velopack;

namespace Stenor;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit.
        VelopackApp.Build().Run();

        // Before anything opens a managed socket (Gemini, update check): may set the
        // process-wide DisableIPv6 switch, which .NET latches on first socket use.
        NetworkGuard.ApplyIpv4FallbackIfNeeded();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
