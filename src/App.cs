using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("ClipNest")]
[assembly: AssemblyDescription("A temporary, private clipboard shelf for Windows")]
[assembly: AssemblyCompany("ClipNest")]
[assembly: AssemblyProduct("ClipNest")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace ClipNest
{
    internal static class App
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        internal static readonly uint ShowMessage = RegisterWindowMessage("ClipNest.Show.1");

        [STAThread]
        public static int Main(string[] args)
        {
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\ClipNest.1" + (demo ? ".Demo" : ""), out created))
            {
                if (!created)
                {
                    PostMessage(new IntPtr(0xffff), ShowMessage, new IntPtr(demo ? 1 : 0), IntPtr.Zero);
                    return 0;
                }
                try
                {
                    Application application = new Application();
                    application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    MainWindow window = new MainWindow(demo);
                    application.Run(window);
                    return 0;
                }
                catch (Exception)
                {
                    MessageBox.Show("ClipNest could not start. It requires Windows 10 or 11 with .NET Framework 4.8. No clipboard history was saved.", "ClipNest", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
