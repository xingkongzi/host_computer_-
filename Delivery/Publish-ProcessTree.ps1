<# SynJMD: Toolhelp process snapshot avoids WMI permissions for publisher cancellation.
   Only descendants of the owned publisher are stopped; WMI-created services are independent. #>
if (-not ('PublishProcessTree' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PublishProcessTree {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)]
    struct Entry {
        public uint size, usage, id; public IntPtr heap; public uint module, threads, parent;
        public int priority; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string name;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto)] static extern bool Process32First(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto)] static extern bool Process32Next(IntPtr h, ref Entry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static int[] Descendants(int root) {
        var entries=new List<Entry>(); var result=new List<int>();
        IntPtr h=CreateToolhelp32Snapshot(2,0);
        if (h==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try {
            Entry e=new Entry(); e.size=(uint)Marshal.SizeOf(e);
            if (Process32First(h,ref e)) do { entries.Add(e); } while(Process32Next(h,ref e));
        } finally { CloseHandle(h); }
        var parents=new Queue<int>(); parents.Enqueue(root);
        while(parents.Count>0) {
            int parent=parents.Dequeue();
            foreach(var e in entries) if(e.parent==parent && !result.Contains((int)e.id)) {
                result.Add((int)e.id); parents.Enqueue((int)e.id);
            }
        }
        result.Reverse(); return result.ToArray();
    }
}
'@
}
