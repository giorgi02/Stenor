using Velopack;

namespace Stenor;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
