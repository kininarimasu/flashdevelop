using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PluginCore.Utilities
{
    public class IconExtractor
    {
        /// <summary>
        /// Gets the system icon specified by index in shell32.dll
        /// </summary>
        public static Icon GetSysIcon(int icNo)
        {
            IntPtr hIcon = Win32.ExtractIcon(Win32.GetModuleHandle(string.Empty), "shell32.dll", icNo);
            Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
            Win32.DestroyIcon(hIcon);
            return icon;
        }

        /// <summary>
        /// Get the associated Icon for a file or application, this method always returns
        /// an icon. If the strPath is invalid or there is no icon, the default icon is returned.
        /// </summary>
        public static Icon GetFileIcon(string path, bool small, bool overlays)
        {
            Win32.SHFILEINFO info = new Win32.SHFILEINFO(true);
            int cbFileInfo = Marshal.SizeOf(info);
            Win32.SHGFI flags = Win32.SHGFI.Icon | Win32.SHGFI.UseFileAttributes;
            if (small) flags |= Win32.SHGFI.SmallIcon; 
            else flags |= Win32.SHGFI.LargeIcon;
            if (overlays) flags |= Win32.SHGFI.AddOverlays; // Get overlays too...
            Win32.SHGetFileInfo(path, 0x00000080, out info, (uint)cbFileInfo, flags);
            Icon icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            Win32.DestroyIcon(info.hIcon);
            return icon;
        }
        public static Icon GetFileIcon(string path, bool small) => GetFileIcon(path, small, false);

        /// <summary>
        /// Get the associated Icon for a file or application, this method always returns
        /// an icon. If the strPath is invalid or there is no icon, the default icon is returned.
        /// </summary>
        public static Icon GetFolderIcon(string path, bool small, bool overlays)
        {
            Win32.SHFILEINFO info = new Win32.SHFILEINFO(true);
            int cbFileInfo = Marshal.SizeOf(info);
            Win32.SHGFI flags = Win32.SHGFI.Icon | Win32.SHGFI.UseFileAttributes;
            if (small) flags |= Win32.SHGFI.SmallIcon; else flags |= Win32.SHGFI.LargeIcon;
            if (overlays) flags |= Win32.SHGFI.AddOverlays; // Get overlays too...
            Win32.SHGetFileInfo(path, 0x00000010, out info, (uint)cbFileInfo, flags);
            Icon icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            Win32.DestroyIcon(info.hIcon);
            return icon;
        }
        public static Icon GetFolderIcon(string path, bool small) => GetFileIcon(path, small, false);
    }
    
}
