using System.Windows.Forms;
using System.IO;

namespace Smena.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Smena.Updater";

        if (!UpdaterOptions.TryParse(args, executableName, out var options, out var errorMessage))
        {
            var message = $"{errorMessage}{Environment.NewLine}{Environment.NewLine}{UpdaterOptions.Usage}";
            MessageBox.Show(
                message,
                "Smena Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Environment.ExitCode = 2;
            return;
        }

        using var form = new MainForm(options);
        Application.Run(form);
        Environment.ExitCode = form.ExitCode;
    }
}
