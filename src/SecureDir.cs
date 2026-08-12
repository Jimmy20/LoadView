using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LoadView
{
    // Directory ACL plumbing for the accurate-CPU-temp feature. Two rules drive all of it:
    //
    //  * The helper runs as SYSTEM, so it must never execute or load code from — nor write into —
    //    a directory a non-admin can write.
    //  * A standard user CAN create folders under C:\ProgramData (its ACL grants Users
    //    (CI)(WD,AD) and gives CREATOR OWNER full control on what they create), so a folder that
    //    already exists when setup runs is untrusted. We take ownership and REPLACE the whole
    //    DACL — `icacls /grant` only merges, and so cannot remove an ACE someone planted first.
    internal static class SecureDir
    {
        // Rights that let a principal tamper with content: write it, delete it, or re-ACL it.
        // (CreateFiles/CreateDirectories are the same bits as WriteData/AppendData.)
        private const FileSystemRights Tamper =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        // NT SERVICE\TrustedInstaller — present on Program Files by default; not a weakness.
        private const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        private static SecurityIdentifier Sid(WellKnownSidType t)
        {
            return new SecurityIdentifier(t, null);
        }

        // A junction/symlink where our folder should be would silently redirect everything the
        // elevated setup writes, so treat one as hostile. Fails closed: if we can't tell, say yes.
        public static bool IsReparsePoint(string path)
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(path);
                if (!di.Exists) return false;
                return (di.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                TempIpc.HelperLog("reparse check failed for " + path + ": " + ex.Message);
                return true;
            }
        }

        // Create (or take over) a directory with a protected DACL: SYSTEM + Administrators full,
        // BUILTIN\Users read+execute — or Modify for the single folder the unprivileged overlay
        // writes its heartbeat into. Must run elevated.
        public static bool Harden(string path, bool usersMayWrite)
        {
            try
            {
                if (IsReparsePoint(path))
                {
                    TempIpc.HelperLog("harden: refusing reparse point " + path);
                    return false;
                }

                DirectoryInfo di = new DirectoryInfo(path);
                if (!di.Exists) di.Create();

                // A fresh DirectorySecurity means SetAccessControl REPLACES the DACL outright.
                DirectorySecurity ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);   // protected, and discard inherited ACEs
                try { ds.SetOwner(Sid(WellKnownSidType.BuiltinAdministratorsSid)); }
                catch (Exception ex) { TempIpc.HelperLog("harden: SetOwner " + path + ": " + ex.Message); }

                Allow(ds, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
                Allow(ds, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
                Allow(ds, WellKnownSidType.BuiltinUsersSid,
                    usersMayWrite ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute);

                di.SetAccessControl(ds);
                return true;
            }
            catch (Exception ex)
            {
                TempIpc.HelperLog("harden " + path + " failed: " + ex.Message);
                return false;
            }
        }

        private static void Allow(DirectorySecurity ds, WellKnownSidType who, FileSystemRights rights)
        {
            ds.AddAccessRule(new FileSystemAccessRule(Sid(who), rights,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        // True when nothing outside SYSTEM / Administrators / TrustedInstaller can modify this
        // directory. The SYSTEM helper checks its own folder and lib\ before loading any assembly,
        // so a mis-provisioned install fails closed instead of loading whatever it happens to find.
        public static bool IsAdminOnly(string path)
        {
            try
            {
                if (IsReparsePoint(path)) return false;
                DirectorySecurity ds = Directory.GetAccessControl(path, AccessControlSections.Access);
                foreach (FileSystemAccessRule r in
                         ds.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    if ((r.FileSystemRights & Tamper) == 0) continue;
                    // Inherit-only ACEs don't grant anything on this folder itself.
                    if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;

                    SecurityIdentifier sid = r.IdentityReference as SecurityIdentifier;
                    if (sid != null && Trusted(sid)) continue;
                    TempIpc.HelperLog("acl: " + path + " is writable by "
                        + (sid == null ? r.IdentityReference.Value : sid.Value));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                TempIpc.HelperLog("acl check " + path + " failed: " + ex.Message);
                return false;
            }
        }

        private static bool Trusted(SecurityIdentifier sid)
        {
            try
            {
                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
                if (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
            }
            catch { }
            return sid.Value == TrustedInstallerSid;
        }
    }
}
