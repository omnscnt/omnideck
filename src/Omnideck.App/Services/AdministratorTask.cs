using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Omnideck.Services;

internal static class AdministratorTask
{
    public const string LaunchArgument = "--scheduled-admin";
    public const string ConfigureArgument = "--configure-admin-task";
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int TaskCreateOrUpdate = 6;
    private const int TaskDontAddPrincipalAce = 16;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskActionExec = 0;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static string TaskName(string sid) => $"Omnideck Administrator ({sid})";

    public static bool IsEnabled => WithCurrentTask(task => task.Enabled && MatchesInstallation(task));

    public static bool TryRun() => WithCurrentTask(task =>
    {
        if (!task.Enabled || !MatchesInstallation(task)) return false;
        task.Run(null);
        return true;
    });

    private static bool MatchesInstallation(dynamic task)
    {
        dynamic definition = task.Definition;
        if (definition.Actions.Count != 1 || definition.Principal.RunLevel != TaskRunLevelHighest) return false;
        dynamic action = definition.Actions.Item(1);
        return action.Type == TaskActionExec
            && string.Equals((string)action.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string)action.WorkingDirectory, InstallLayout.AppDirectory, StringComparison.OrdinalIgnoreCase)
            && action.Arguments == LaunchArgument;
    }

    private static bool WithCurrentTask(Func<dynamic, bool> action)
    {
        if (!IsInProgramFiles) return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            dynamic folder = GetRootFolder();
            return action(folder.GetTask(TaskName(identity.User!.Value)));
        }
        catch (Exception ex) when (ex.HResult == FileNotFound)
        {
            return false;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            CrashLog.Write("administrator task", ex);
            return false;
        }
    }

    public static void Configure(bool enabled, string expectedSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        if (sid != expectedSid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Use the administrator account that runs Omnideck.");

        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        string name = TaskName(sid);
        if (!enabled)
        {
            try { folder.DeleteTask(name, 0); }
            catch (Exception ex) when (ex.HResult == FileNotFound) { }
            return;
        }

        SecureInstallation(identity.User!);
        dynamic definition = scheduler.NewTask(0);
        definition.RegistrationInfo.Description = "Starts Omnideck with administrator rights for global hotkeys.";
        definition.Principal.UserId = sid;
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelHighest;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = Environment.ProcessPath!;
        action.Arguments = LaunchArgument;
        action.WorkingDirectory = InstallLayout.AppDirectory;

        // The filtered user token may run the fixed command. Only administrators and SYSTEM can edit it.
        string security = $"O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})";
        folder.RegisterTaskDefinition(name, definition, TaskCreateOrUpdate | TaskDontAddPrincipalAce,
            sid, null, TaskLogonInteractiveToken, security);
    }

    public static bool IsInProgramFiles =>
        Path.GetFullPath(InstallLayout.Root).StartsWith(ProgramFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static void SecureInstallation(SecurityIdentifier user)
    {
        if (!IsInProgramFiles) throw new UnauthorizedAccessException("Install Omnideck in Program Files first.");

        var root = new DirectoryInfo(InstallLayout.Root);
        var parents = new Stack<DirectoryInfo>();
        for (DirectoryInfo? parent = root.Parent; parent != null; parent = parent.Parent)
        {
            parents.Push(parent);
            if (string.Equals(parent.FullName, ProgramFiles, StringComparison.OrdinalIgnoreCase)) break;
        }
        foreach (var parent in parents) SecureEntry(parent, user: null);

        SecureTree(root, user);
    }

    internal static void SecureUpdatePayload(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecureTree(new DirectoryInfo(path), identity.User!);
    }

    private static void SecureTree(DirectoryInfo directory, SecurityIdentifier user)
    {
        SecureEntry(directory, user);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            if (entry is DirectoryInfo child) SecureTree(child, user);
            else SecureEntry(entry, user);
    }

    private static void SecureEntry(FileSystemInfo entry, SecurityIdentifier? user)
    {
        const FileSystemRights genericWriteOrAll = (FileSystemRights)0x50000000;
        const FileSystemRights write = genericWriteOrAll | FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        entry.Refresh();
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw Unprotected(entry);
        FileSystemSecurity security = entry is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        bool changeOwner = user != null && owner.Equals(user);
        if (!changeOwner && !IsTrusted(owner)) throw Unprotected(entry);

        bool removeCreatorGrant = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                (rule.FileSystemRights & write) == 0 || IsTrusted((SecurityIdentifier)rule.IdentityReference)) continue;

            // Explorer can keep the original owner on copied files. Program Files grants that owner Full Control through CREATOR OWNER.
            if (!changeOwner || !rule.IsInherited || !IsCreatorGrant(rule, owner)) throw Unprotected(entry);
            removeCreatorGrant = true;
        }

        if (!changeOwner) return;
        if (removeCreatorGrant)
        {
            // RemoveAccessRuleSpecific cannot remove an inherited ACE. Preserve the other entries as-is.
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            var acl = descriptor.DiscretionaryAcl!;
            bool removed = false;
            for (int i = acl.Count - 1; i >= 0; i--)
            {
                if (acl[i] is CommonAce ace && ace.AceFlags == AceFlags.Inherited &&
                    ace.AceQualifier == AceQualifier.AccessAllowed && ace.SecurityIdentifier.Equals(owner) &&
                    ace.AccessMask == (int)FileSystemRights.FullControl)
                {
                    acl.RemoveAce(i);
                    removed = true;
                }
            }
            if (!removed) throw Unprotected(entry);
            descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclProtected);
            byte[] buffer = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(buffer, 0);
            security.SetSecurityDescriptorBinaryForm(buffer, AccessControlSections.Access);
        }

        security.SetOwner(Administrators);
        if (entry is DirectoryInfo folder) folder.SetAccessControl((DirectorySecurity)security);
        else ((FileInfo)entry).SetAccessControl((FileSecurity)security);
        SecureEntry(entry, user: null);
    }

    private static bool IsCreatorGrant(FileSystemAccessRule rule, SecurityIdentifier owner) =>
        rule.IdentityReference.Equals(owner) && rule.AccessControlType == AccessControlType.Allow &&
        rule.FileSystemRights == FileSystemRights.FullControl && rule.InheritanceFlags == InheritanceFlags.None &&
        rule.PropagationFlags == PropagationFlags.None;

    private static UnauthorizedAccessException Unprotected(FileSystemInfo entry) =>
        new($"Unprotected installation path: {entry.FullName}");

    private static bool IsTrusted(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller

    private static dynamic GetRootFolder() => Connect().GetFolder("\\");

    private static dynamic Connect()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        scheduler.Connect();
        return scheduler;
    }
}
