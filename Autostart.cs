using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace CyberMonitor;

/// <summary>
/// Starts CyberMonitor at log on through a Task Scheduler task (needed because the app
/// runs elevated, which the registry Run key cannot do without a UAC prompt).
/// </summary>
static class Autostart
{
    const string TaskName = "CyberMonitor";

    /// <summary>The vendor app registers this task and would grab the COM port first.</summary>
    const string VendorTaskName = "UsbMonitor";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts CyberMonitor (3.5" USB display dashboard) at log on.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "CyberMonitor-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0) return false;
        }
        finally { File.Delete(file); }

        // Keep the vendor app from starting too; disabling (not deleting) keeps it reversible.
        Schtasks($"/Change /TN \"{VendorTaskName}\" /DISABLE");
        return true;
    }

    public static bool Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
