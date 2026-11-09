using System.Runtime.InteropServices;

namespace NettoyeurPC.Core;

/// <summary>Appels directs à libimobiledevice (DLL embarquées) pour ce que les outils en ligne de commande ne savent pas faire,
/// comme demander la taille des applis et de leurs données à l'iPhone.</summary>
static class IPhoneNative
{
    const string Imd = "libimobiledevice-1.0.dll";
    const string Pl = "libplist-2.0.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);

    [DllImport(Imd, CallingConvention = CallingConvention.Cdecl)] static extern int idevice_new(out IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string udid);
    [DllImport(Imd, CallingConvention = CallingConvention.Cdecl)] static extern int idevice_free(IntPtr device);
    [DllImport(Imd, CallingConvention = CallingConvention.Cdecl)] static extern int instproxy_client_start_service(IntPtr device, out IntPtr client, [MarshalAs(UnmanagedType.LPStr)] string label);
    [DllImport(Imd, CallingConvention = CallingConvention.Cdecl)] static extern int instproxy_client_free(IntPtr client);
    [DllImport(Imd, CallingConvention = CallingConvention.Cdecl)] static extern int instproxy_browse(IntPtr client, IntPtr options, out IntPtr result);

    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr plist_new_dict();
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr plist_new_array();
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr plist_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern void plist_dict_set_item(IntPtr dict, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, IntPtr item);
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern void plist_array_append_item(IntPtr arr, IntPtr item);
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern int plist_to_xml(IntPtr plist, out IntPtr xml, out uint length);
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern void plist_mem_free(IntPtr ptr);
    [DllImport(Pl, CallingConvention = CallingConvention.Cdecl)] static extern void plist_free(IntPtr plist);

    static bool _dirSet;

    /// <summary>Renvoie la liste des applis utilisateur au format XML plist, avec leurs tailles.</summary>
    public static string BrowseAppsXml(string udid)
    {
        if (!_dirSet) { SetDllDirectory(IPhone.ToolsDir); _dirSet = true; }
        IntPtr dev = IntPtr.Zero, ipc = IntPtr.Zero, opts = IntPtr.Zero, res = IntPtr.Zero;
        try
        {
            var e = idevice_new(out dev, udid);
            if (e != 0 || dev == IntPtr.Zero) throw new Exception($"iPhone introuvable (code {e}).");
            e = instproxy_client_start_service(dev, out ipc, "NettoyeurPC");
            if (e != 0 || ipc == IntPtr.Zero) throw new Exception($"Service des applis inaccessible (code {e}). Déverrouille l'iPhone.");
            opts = plist_new_dict();
            plist_dict_set_item(opts, "ApplicationType", plist_new_string("User"));
            var attrs = plist_new_array();
            foreach (var a in new[] { "CFBundleIdentifier", "CFBundleDisplayName", "CFBundleName", "CFBundleShortVersionString", "StaticDiskUsage", "DynamicDiskUsage" })
                plist_array_append_item(attrs, plist_new_string(a));
            plist_dict_set_item(opts, "ReturnAttributes", attrs);
            e = instproxy_browse(ipc, opts, out res);
            if (e != 0 || res == IntPtr.Zero) throw new Exception($"L'iPhone n'a pas renvoyé la liste (code {e}).");
            plist_to_xml(res, out var xml, out var len);
            try { return Marshal.PtrToStringUTF8(xml, (int)len); }
            finally { plist_mem_free(xml); }
        }
        finally
        {
            if (res != IntPtr.Zero) plist_free(res);
            if (opts != IntPtr.Zero) plist_free(opts);
            if (ipc != IntPtr.Zero) instproxy_client_free(ipc);
            if (dev != IntPtr.Zero) idevice_free(dev);
        }
    }
}
