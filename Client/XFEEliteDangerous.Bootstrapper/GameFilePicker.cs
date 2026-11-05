using System.Runtime.InteropServices;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Bootstrapper;

internal static class GameFilePicker
{
    internal static Task<string?> PickAsync()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Pick()); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static unsafe string? Pick()
    {
        var buffer = new char[32768];
        var caption = AppText.Get("Launch.SelectOdyssey");
        const string filter = "Elite Dangerous: Odyssey\0EliteDangerous64.exe\0\0";
        fixed (char* file = buffer, title = caption, mask = filter)
        {
            var dialog = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName), Filter = mask, FilterIndex = 1, File = file, MaxFile = (uint)buffer.Length, Title = title,
                // Explorer layout; require the file/path; do not add the selection to Recent Items.
                Flags = 0x80000 | 0x1000 | 0x800 | 0x02000000 | 0x8
            };
            if (GetOpenFileNameW(ref dialog) != 0) return new string(file);
            var error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException(AppText.Get("Launch.GamePickerFailed") + "0x" + error.ToString("X"));
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OpenFileName
    {
        public uint Size;
        public nint Owner, Instance;
        public char* Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDirectory, Title;
        public uint Flags;
        public ushort FileOffset, FileExtension;
        public char* DefaultExtension;
        public nint CustomData, Hook;
        public char* Template;
        public nint Reserved;
        public uint ReservedFlags, ExtendedFlags;
    }

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern int GetOpenFileNameW(ref OpenFileName dialog);
    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern uint CommDlgExtendedError();
}
