using System.Windows;

namespace KVANTLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ex) => 
        {
            System.Windows.MessageBox.Show("Критическая ошибка: " + ex.ExceptionObject.ToString(), "Квант Лаунчер - Ошибка");
        };

        base.OnStartup(e);
    }
}
