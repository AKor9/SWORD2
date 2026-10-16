using System.Runtime.InteropServices;

namespace Sword.Engine.Platform.Win32;

/// <summary>
/// Системные окна сообщений и выбора файлов (окна инструментов; игре не нужны). Диалог выбора — на отдельном STA-потоке,
/// вызов блокирует до закрытия: чтобы окно приложения рисовалось, зовите из фонового потока.
/// </summary>
public static class NativeDialogs
{
    private const uint MB_ICONERROR = 0x00000010, MB_ICONQUESTION = 0x00000020, MB_YESNO = 0x00000004, MB_SETFOREGROUND = 0x00010000;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    public static void Error(string caption, string text) => MessageBoxW(nint.Zero, text, caption, MB_ICONERROR | MB_SETFOREGROUND);

    /// <summary>Вопрос «да/нет».</summary>
    public static bool Ask(string caption, string text, nint owner = 0) =>
        MessageBoxW(owner, text, caption, MB_ICONQUESTION | MB_YESNO | MB_SETFOREGROUND) == IDYES;

    /// <summary>Выбрать файлы.</summary>
    /// <param name="filter">Маски через «;» («*.ttf;*.otf»); пусто — любые файлы.</param>
    /// <returns>Полные пути; отмена — null.</returns>
    public static string[]? OpenFiles(nint owner, string title, bool multiple, string filter = "", string? folder = null) =>
        OnSta(() => Show(owner, title, pickFolders: false, multiple, filter, folder));

    /// <summary>Выбрать каталог; отмена — null.</summary>
    public static string? PickFolder(nint owner, string title, string? folder = null) =>
        OnSta(() => Show(owner, title, pickFolders: true, multiple: false, "", folder))?.FirstOrDefault();

    private static T? OnSta<T>(Func<T?> work) where T : class
    {
        T? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("окно выбора файла: " + error.Message, error);
        return result;
    }

    private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200,
        FOS_PATHMUSTEXIST = 0x800, FOS_FILEMUSTEXIST = 0x1000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    private static string[]? Show(nint owner, string title, bool pickFolders, bool multiple, string filter, string? folder)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
        try
        {
            dialog.GetOptions(out uint options);
            options |= FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
            options |= pickFolders ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST;
            if (multiple) options |= FOS_ALLOWMULTISELECT;
            dialog.SetOptions(options);
            dialog.SetTitle(title);
            if (!pickFolders && filter.Length > 0)
            {
                var specs = new[]
                {
                    new FilterSpec { Name = filter, Spec = filter },
                    new FilterSpec { Name = "Все файлы", Spec = "*.*" },
                };
                dialog.SetFileTypes((uint)specs.Length, specs);
            }
            if (folder is not null && Directory.Exists(folder)
                && SHCreateItemFromParsingName(folder, 0, typeof(IShellItem).GUID, out var folderItem) == 0)
            {
                dialog.SetFolder(folderItem);
                Marshal.ReleaseComObject(folderItem);
            }

            int hr = dialog.Show(owner);
            if (hr == ERROR_CANCELLED) return null;
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResults(out var items);
            try
            {
                items.GetCount(out uint count);
                var paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    items.GetItemAt(i, out var item);
                    item.GetDisplayName(SIGDN_FILESYSPATH, out nint name);
                    paths[i] = Marshal.PtrToStringUni(name) ?? "";
                    Marshal.FreeCoTaskMem(name);
                    Marshal.ReleaseComObject(item);
                }
                return paths;
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass;

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(nint parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid riid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid riid, out nint result);
        void GetPropertyStore(int flags, ref Guid riid, out nint store);
        void GetPropertyDescriptionList(nint key, ref Guid riid, out nint list);
        void GetAttributes(int flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out nint items);
    }
}
