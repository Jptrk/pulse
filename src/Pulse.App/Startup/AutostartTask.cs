using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace Pulse.App.Startup;

/// <summary>
/// Start-at-logon via Task Scheduler with highest privileges, so Pulse starts elevated without a UAC prompt each boot.
/// Registered through the Task Scheduler COM API from an in-memory XML string — no temp file that another process
/// could swap before an elevated registration reads it. The defaults Task Scheduler would apply
/// (72 h time limit, below-normal priority) are overridden.
/// </summary>
internal static class AutostartTask
{
    private const string TaskName = "Pulse Hardware Monitor";
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    public static bool IsEnabled()
    {
        try
        {
            RootFolder().GetTask(TaskName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Enable(out string error)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Pulse hardware monitor at logon.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
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
                <Priority>4</Priority>
                <AllowStartOnDemand>true</AllowStartOnDemand>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--minimized</Arguments></Exec>
              </Actions>
            </Task>
            """;
        try
        {
            RootFolder().RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Disable(out string error)
    {
        try
        {
            RootFolder().DeleteTask(TaskName, 0);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static dynamic RootFolder()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }
}

internal static class Elevation
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <returns>True if an elevated copy was launched (the caller should exit).</returns>
    public static bool TryRelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt declined
        }
    }
}
