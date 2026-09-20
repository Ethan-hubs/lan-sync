using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class AutoStartPolicyTests
{
    [TestMethod]
    public void Startup_never_writes_and_only_deletes_a_stale_entry()
    {
        Assert.AreEqual(AutoStartAction.None, AutoStartPolicy.DecideStartup(null, executableExists: false));
        Assert.AreEqual(AutoStartAction.None, AutoStartPolicy.DecideStartup("\"C:\\Apps\\LanSync.exe\"", executableExists: true));
        Assert.AreEqual(AutoStartAction.Delete, AutoStartPolicy.DecideStartup("\"C:\\Missing\\LanSync.exe\"", executableExists: false));
        Assert.AreEqual(AutoStartAction.Delete, AutoStartPolicy.DecideStartup(string.Empty, executableExists: false));
    }

    [TestMethod]
    public void Menu_choice_is_the_only_write_delete_decision()
    {
        Assert.AreEqual(AutoStartAction.Write, AutoStartPolicy.DecideMenuChange(enabled: true));
        Assert.AreEqual(AutoStartAction.Delete, AutoStartPolicy.DecideMenuChange(enabled: false));
    }

    [TestMethod]
    public void Run_command_path_is_parsed_and_formatted_without_losing_spaces()
    {
        const string path = @"C:\Program Files\LanSync\LanSync.Tray.exe";

        var command = AutoStartPolicy.FormatRunCommand(path);

        Assert.AreEqual("\"C:\\Program Files\\LanSync\\LanSync.Tray.exe\"", command);
        Assert.AreEqual(path, AutoStartPolicy.GetExecutablePath(command));
        Assert.AreEqual(@"C:\LanSync\Tray.exe", AutoStartPolicy.GetExecutablePath(@"C:\LanSync\Tray.exe --minimized"));
        Assert.IsNull(AutoStartPolicy.GetExecutablePath("\"unterminated"));
    }
}
