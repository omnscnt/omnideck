using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Omnideck.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", "omnideck.exe");
        if (!File.Exists(app))
        {
            MessageBox(IntPtr.Zero, $"Can't find {app}.\nReinstall omnideck from its release zip.", "omnideck.", 0x10 /* MB_ICONERROR */);
            return 1;
        }
        // Started by the user, so this process may take the foreground; the app it starts inherits that.
        Process.Start(new ProcessStartInfo(app, string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app),
        });
        return 0;
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
