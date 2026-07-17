namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Best-effort file/directory permission hardening for secret material on the data volume.
/// </summary>
/// <remarks>
/// На Unix виставляє <c>0600</c> (файли) / <c>0700</c> (каталог) через
/// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>. На Windows це NO-OP (best-effort):
/// покладаємось на ACL самого тому/каталогу даних — задокументовано у deploy/DEPLOYMENT.md.
/// </remarks>
internal static class FilePermissions
{
    /// <summary>Restricts a file to owner read/write only (<c>0600</c> on Unix; NO-OP on Windows).</summary>
    public static void HardenFileToOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Restricts a directory to owner access only (<c>0700</c> on Unix; NO-OP on Windows).</summary>
    public static void HardenDirectoryToOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
