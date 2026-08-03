using System;
using System.Windows;

namespace BinaryRead
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            window.Show();

            if (e.Args.Length > 0)
            {
                window.OpenFile(e.Args[0]);
            }
        }
    }
}

