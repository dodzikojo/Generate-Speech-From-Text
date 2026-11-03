using System.Runtime.InteropServices;

namespace IssueLab.VoiceBench.Infrastructure.Packaging;

/// <summary>Moves files somewhere the operator can still restore them from.</summary>
public interface IFileRecycler
{
    /// <summary>
    /// Recycles every file, or throws <see cref="IOException"/>. A file that can't be recycled is
    /// left where it is; it is never deleted permanently instead.
    /// </summary>
    void Recycle(IReadOnlyList<string> fullPaths);
}

/// <summary>
/// The Windows Recycle Bin through <c>IFileOperation</c>. The shell silently deletes permanently
/// when it can't recycle (a drive without a Recycle Bin, a file larger than the bin, the "don't
/// move files to the Recycle Bin" setting), so every delete is checked first and refused unless
/// it will be recycled.
/// </summary>
public sealed class WindowsRecycleBin : IFileRecycler
{
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_EARLYFAILURE = 0x00100000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const int E_ABORT = unchecked((int)0x80004004);

    public void Recycle(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        if (fullPaths.Count == 0) return;
        foreach (string path in fullPaths)
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Recycle needs full paths.", nameof(fullPaths));

        // Shell file operations belong on an STA thread.
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try { RecycleOnThisThread(fullPaths); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true, Name = "VoiceBench recycle" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        if (failure is IOException) throw failure;
        if (failure is not null) throw new IOException(failure.Message, failure);
    }

    private static void RecycleOnThisThread(IReadOnlyList<string> fullPaths)
    {
        IFileOperation operation = (IFileOperation)new FileOperation();
        RecycleOnlySink sink = new();
        uint cookie = 0;
        try
        {
            operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE);
            cookie = operation.Advise(sink);
            Guid shellItem = typeof(IShellItem).GUID;
            foreach (string path in fullPaths)
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItem, out IShellItem item);
                operation.DeleteItem(item, null);
            }

            try
            {
                operation.PerformOperations();
            }
            catch (COMException exception)
            {
                throw sink.Refused.Count > 0
                    ? new IOException($"“{Path.GetFileName(sink.Refused[0])}” can't go to the Recycle Bin on this drive, so it was kept.", exception)
                    : new IOException($"Windows could not move the files to the Recycle Bin (0x{exception.HResult:X8}).", exception);
            }

            if (sink.Refused.Count > 0)
                throw new IOException($"“{Path.GetFileName(sink.Refused[0])}” can't go to the Recycle Bin on this drive, so it was kept.");
            if (operation.GetAnyOperationsAborted())
                throw new IOException("Moving the files to the Recycle Bin was cancelled.");
        }
        finally
        {
            if (cookie != 0) operation.Unadvise(cookie);
            Marshal.FinalReleaseComObject(operation);
        }
    }

    /// <summary>Lets a delete go ahead only when the shell says it will recycle the item.</summary>
    [ComVisible(true)]
    private sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        public List<string> Refused { get; } = [];

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            if ((dwFlags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return 0;
            psiItem.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out IntPtr name);
            try { Refused.Add(Marshal.PtrToStringUni(name) ?? "file"); }
            finally { Marshal.FreeCoTaskMem(name); }
            return E_ABORT;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => 0;
        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated) => 0;
        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated) => 0;
        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;
        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated) => 0;
        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated) => 0;
        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) => 0;
        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem) => 0;
        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperation;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IFileOperationProgressSink? pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, IShellItem psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrMove, IShellItem psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrCopy, IShellItem psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
