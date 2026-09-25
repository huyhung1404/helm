namespace Helm.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirstInstance)
        {
            instance.SignalFirstInstance();
            return 0;
        }

        var app = new App(instance, args);
        app.InitializeComponent();
        return app.Run();
    }
}
