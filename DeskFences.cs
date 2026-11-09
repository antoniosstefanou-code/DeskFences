// DeskFences - απλό εργαλείο τύπου "Fences" για την επιφάνεια εργασίας των Windows.
// Κάθε fence είναι ένα παράθυρο πάνω στο desktop που δείχνει τα περιεχόμενα ενός πραγματικού φακέλου
// (%USERPROFILE%\DeskFences\<όνομα>). Σύρε αρχεία από το desktop μέσα σε ένα fence για να μετακινηθούν εκεί.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using VBFS = Microsoft.VisualBasic.FileIO.FileSystem;

public class Rect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public class FenceData
{
    public string Name { get; set; }
    public string Folder { get; set; }
    // Παλιά μορφή (μία θέση για όλες τις οθόνες) - διαβάζεται μόνο για μετάπτωση.
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    // Θέση/μέγεθος ανά συνδυασμό οθονών (κλειδί: ScreenLayout.Current()).
    public Dictionary<string, Rect> Layouts { get; set; }
    public int Color { get; set; }
    public int Opacity { get; set; }
    public bool Locked { get; set; }
}

// Κάθε συνδυασμός οθονών (γραφείο, Remote Desktop από το σπίτι κ.λπ.) έχει δική του διάταξη.
// Για συνδυασμό που δεν έχει δει ποτέ, η διάταξη υπολογίζεται αναλογικά από μια αποθηκευμένη, χωρίς να αποθηκευτεί.
static class ScreenLayout
{
    class Mon { public Rectangle B; public bool Primary; public int Dpi; }

    // "v2|x,y,w,h,dpi[*];..." : οθόνες σε πραγματικά pixels, με την κλίμακα (DPI) της καθεμιάς.
    // Η παλιά μορφή "dpi|x,y,w,h[*];..." είχε συντεταγμένες κλιμακωμένες για την κύρια οθόνη.
    const string Version = "v2|";

    public static string Current()
    {
        var parts = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select(s => s.Bounds.X + "," + s.Bounds.Y + "," + s.Bounds.Width + "," + s.Bounds.Height + ","
                + Native.DpiFor(s.Bounds) + (s.Primary ? "*" : ""));
        return Version + string.Join(";", parts.ToArray());
    }

    public static bool IsCurrentFormat(string sig) { return sig.StartsWith(Version); }

    static List<Mon> Monitors(string sig)
    {
        bool v2 = IsCurrentFormat(sig);
        string[] head = sig.Split('|');
        int globalDpi = v2 ? 0 : int.Parse(head[0]);
        var list = new List<Mon>();
        foreach (var p in head[1].Split(';'))
        {
            var n = p.TrimEnd('*').Split(',').Select(int.Parse).ToArray();
            list.Add(new Mon { B = new Rectangle(n[0], n[1], n[2], n[3]), Primary = p.EndsWith("*"), Dpi = v2 ? n[4] : globalDpi });
        }
        return list;
    }

    // Μετατρέπει θέση της παλιάς μορφής στις πραγματικές συντεταγμένες των τωρινών οθονών (ίδιος αριθμός οθονών).
    public static Rect FromLegacy(string oldSig, Rect r)
    {
        var from = Monitors(oldSig);
        var to = Monitors(Current());
        if (from.Count != to.Count) return null;
        var c = new Point(r.X + r.W / 2, r.Y + r.H / 2);
        int i = from.FindIndex(m => m.B.Contains(c));
        if (i < 0) i = from.FindIndex(m => m.B.IntersectsWith(new Rectangle(r.X, r.Y, r.W, r.H)));
        if (i < 0) return null;
        Rectangle s = from[i].B, t = to[i].B;
        double f = t.Width / (double)s.Width;
        return new Rect
        {
            X = t.X + (int)Math.Round((r.X - s.X) * f), Y = t.Y + (int)Math.Round((r.Y - s.Y) * f),
            W = (int)Math.Round(r.W * f), H = (int)Math.Round(r.H * f)
        };
    }

    public static Rectangle For(FenceData d)
    {
        string cur = Current();
        Rect r;
        if (d.Layouts.TryGetValue(cur, out r)) return ToRectangle(r);
        if (d.Layouts.Count == 0) return new Rectangle(80, 80, 340, 240);

        // Προτιμάται διάταξη με τον ίδιο αριθμό οθονών, μετά όποια αποθηκεύτηκε πρώτη.
        int curCount = Monitors(cur).Count;
        string src = d.Layouts.Keys.OrderByDescending(k => Monitors(k).Count == curCount).First();
        return Scale(ToRectangle(d.Layouts[src]), src, cur);
    }

    static Rectangle Scale(Rectangle r, string fromSig, string toSig)
    {
        var from = Monitors(fromSig);
        var to = Monitors(toSig);
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        Mon s = from.FirstOrDefault(m => m.B.Contains(c))
             ?? from.OrderBy(m => Math.Abs(m.B.X + m.B.Width / 2 - c.X) + Math.Abs(m.B.Y + m.B.Height / 2 - c.Y)).First();
        Mon t = MapMonitor(s, from, to);

        // Η θέση ακολουθεί την αναλογία της οθόνης· το μέγεθος ακολουθεί το DPI (το περιεχόμενο μεγαλώνει/μικραίνει με αυτό).
        double sx = t.B.Width / (double)s.B.Width, sy = t.B.Height / (double)s.B.Height;
        double k = t.Dpi / (double)s.Dpi;
        int w = Math.Min(t.B.Width, Math.Max(140, (int)Math.Round(r.Width * k)));
        int h = Math.Min(t.B.Height, Math.Max(80, (int)Math.Round(r.Height * k)));
        int x = t.B.X + (int)Math.Round((r.X - s.B.X) * sx);
        int y = t.B.Y + (int)Math.Round((r.Y - s.B.Y) * sy);
        x = Math.Max(t.B.Left, Math.Min(x, t.B.Right - w));
        y = Math.Max(t.B.Top, Math.Min(y, t.B.Bottom - h));
        return new Rectangle(x, y, w, h);
    }

    // Κύρια -> κύρια· οι υπόλοιπες με τη σειρά από αριστερά προς τα δεξιά· όσες περισσεύουν πάνε στην κύρια.
    static Mon MapMonitor(Mon s, List<Mon> from, List<Mon> to)
    {
        Mon primary = to.FirstOrDefault(m => m.Primary) ?? to[0];
        if (s.Primary) return primary;
        var fromOthers = from.Where(m => !m.Primary).ToList();
        var toOthers = to.Where(m => !m.Primary).ToList();
        int i = fromOthers.IndexOf(s);
        return i >= 0 && i < toOthers.Count ? toOthers[i] : primary;
    }

    public static Rectangle ToRectangle(Rect r) { return new Rectangle(r.X, r.Y, r.W, r.H); }
    public static Rect FromRectangle(Rectangle r) { return new Rect { X = r.X, Y = r.Y, W = r.Width, H = r.Height }; }
}

public class AppConfig
{
    public List<FenceData> Fences { get; set; }
    // Μέγεθος εικονιδίων σε λογικά pixels (στο 100%)· 0 = προεπιλογή (32, όπως τα μεσαία του desktop).
    public int IconSize { get; set; }
    // false (προεπιλογή): τα εικονίδια της επιφάνειας εργασίας μετακινούνται αυτόματα έξω από τα fences.
    public bool AllowIconsUnder { get; set; }
    public AppConfig() { Fences = new List<FenceData>(); }
}

// Κρατά τα εικονίδια της επιφάνειας εργασίας έξω από τα fences: όποιο πέφτει κάτω από fence
// μετακινείται (μόνο η θέση του, όχι το αρχείο) στην πλησιέστερη ελεύθερη θέση του πλέγματος.
// Χρησιμοποιεί το IFolderView της επιφάνειας εργασίας του Explorer.
static class DesktopIcons
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object loc,
            [In, MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid sid, ref Guid iid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6();
        void _7(); void _8(); void _9(); void _10(); void _11(); void _12();
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView { IntPtr GetWindow(); }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void _GetCurrentViewMode(); void _SetCurrentViewMode(); void _GetFolder();
        IntPtr Item(int index);
        int ItemCount(uint flags);
        void _Items(); void _GetSelectionMarkedItem(); void _GetFocusedItem();
        POINT GetItemPosition(IntPtr pidl);
        POINT GetSpacing();
        void _GetDefaultSpacing();
        [PreserveSig] int GetAutoArrange();
        void _SelectItem();
        void SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [MarshalAs(UnmanagedType.LPArray)] POINT[] apt, uint flags);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

    static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    const uint SVGIO_ALLVIEW = 2, SVSI_POSITIONITEM = 0x80;

    static string logPath;

    // fences: θέσεις των fences σε συντεταγμένες οθόνης. Επιστρέφει πόσα εικονίδια μετακινήθηκαν.
    public static int KeepOutOf(List<Rectangle> fences, string log)
    {
        logPath = log;
        if (fences.Count == 0) return 0;
        object sw = null, disp = null, sb = null, sv = null;
        var pidls = new List<IntPtr>();
        try
        {
            sw = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));
            object loc = 0 /* CSIDL_DESKTOP */, root = null;
            int hwnd;
            disp = ((IShellWindows)sw).FindWindowSW(ref loc, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
            if (disp == null) return 0;
            Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
            sb = ((IComServiceProvider)disp).QueryService(ref sid, ref iid);
            sv = ((IShellBrowser)sb).QueryActiveShellView();
            var fv = (IFolderView)sv;
            if (fv.GetAutoArrange() == 0) return 0; // S_OK: αυτόματη τακτοποίηση ενεργή, τα Windows ορίζουν τις θέσεις

            IntPtr lv = FindWindowEx(((IShellView)sv).GetWindow(), IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return 0;
            var origin = new POINT();
            ClientToScreen(lv, ref origin);
            POINT sp = fv.GetSpacing();
            if (sp.X <= 0 || sp.Y <= 0) return 0;

            int n = fv.ItemCount(SVGIO_ALLVIEW);
            var cells = new List<Rectangle>();
            for (int i = 0; i < n; i++)
            {
                IntPtr pidl = fv.Item(i);
                pidls.Add(pidl);
                POINT p = fv.GetItemPosition(pidl);
                cells.Add(new Rectangle(p.X + origin.X, p.Y + origin.Y, sp.X, sp.Y));
            }

            // Το πλέγμα των Windows: η μετατόπιση που έχουν τα περισσότερα εικονίδια.
            int offX = Mode(cells.Select(c => Mod(c.X - origin.X, sp.X)));
            int offY = Mode(cells.Select(c => Mod(c.Y - origin.Y, sp.Y)));

            int moved = 0;
            for (int i = 0; i < n; i++)
            {
                Rectangle c = cells[i];
                Rectangle core = Rectangle.Inflate(c, -sp.X / 4, -sp.Y / 4);
                if (!fences.Any(f => f.IntersectsWith(core))) continue;

                Rectangle? best = null; long bestD = long.MaxValue;
                foreach (var scr in Screen.AllScreens)
                {
                    Rectangle wa = scr.WorkingArea;
                    int x0 = origin.X + offX + (int)Math.Ceiling((wa.Left - origin.X - offX) / (double)sp.X) * sp.X;
                    int y0 = origin.Y + offY + (int)Math.Ceiling((wa.Top - origin.Y - offY) / (double)sp.Y) * sp.Y;
                    for (int x = x0; x + sp.X <= wa.Right; x += sp.X)
                        for (int y = y0; y + sp.Y <= wa.Bottom; y += sp.Y)
                        {
                            long dx = x - c.X, dy = y - c.Y, d = dx * dx + dy * dy;
                            if (d >= bestD) continue;
                            var cand = new Rectangle(x, y, sp.X, sp.Y);
                            if (fences.Any(f => f.IntersectsWith(cand))) continue;
                            var inner = Rectangle.Inflate(cand, -2, -2);
                            bool taken = false;
                            for (int j = 0; j < cells.Count && !taken; j++)
                                if (j != i && cells[j].IntersectsWith(inner)) taken = true;
                            if (taken) continue;
                            best = cand; bestD = d;
                        }
                }
                if (best == null) { Log("Δεν βρέθηκε ελεύθερη θέση για εικονίδιο στο " + c.Location); continue; }

                var pt = new POINT { X = best.Value.X - origin.X, Y = best.Value.Y - origin.Y };
                fv.SelectAndPositionItems(1, new[] { pidls[i] }, new[] { pt }, SVSI_POSITIONITEM);
                Log("Εικονίδιο από " + c.Location + " σε " + best.Value.Location);
                cells[i] = best.Value;
                moved++;
            }
            return moved;
        }
        catch (Exception ex) { Log("Σφάλμα: " + ex.Message); return 0; }
        finally
        {
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            foreach (var o in new[] { sv, sb, disp, sw })
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    static int Mod(int a, int m) { return ((a % m) + m) % m; }

    static int Mode(IEnumerable<int> values)
    {
        var g = values.GroupBy(v => v).OrderByDescending(x => x.Count()).FirstOrDefault();
        return g == null ? 0 : g.Key;
    }

    static string lastLog;
    static void Log(string msg)
    {
        if (logPath == null || msg == lastLog) return;
        lastLog = msg;
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 200000) File.Delete(logPath);
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}

static class Native
{
    public const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_WINDOWPOSCHANGING = 0x46,
        WM_DPICHANGED = 0x02E0, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, LVM_SETICONSPACING = 0x1035;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const uint SWP_NOZORDER = 0x0004;
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public const int GWL_HWNDPARENT = -8;
    public const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_ADDOVERLAYS = 0x20,
        SHGFI_SYSICONINDEX = 0x4000, SHGFI_OVERLAYINDEX = 0x40;
    public const int SHIL_LARGE = 0, SHIL_EXTRALARGE = 2, ILD_TRANSPARENT = 1;
    public static Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // Per-monitor V2: συντεταγμένες σε πραγματικά pixels σε κάθε οθόνη, ανεξάρτητα από την κλίμακά της.
    public static void EnableDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
        catch (EntryPointNotFoundException) { }
        SetProcessDPIAware();
    }

    public static int DpiFor(Rectangle r)
    {
        try
        {
            var rc = new RECT { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom };
            uint x, y;
            if (GetDpiForMonitor(MonitorFromRect(ref rc, 2 /* MONITOR_DEFAULTTONEAREST */), 0, out x, out y) == 0) return (int)x;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return (int)g.DpiX;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    // Ο δείκτης IImageList που επιστρέφει το SHGetImageList μπορεί να χρησιμοποιηθεί ως HIMAGELIST.
    [DllImport("shell32.dll")] public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);
    [DllImport("comctl32.dll")] public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int i, int v);

    public static void SetOwner(IntPtr hwnd, IntPtr owner)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_HWNDPARENT, owner);
        else SetWindowLong32(hwnd, GWL_HWNDPARENT, owner.ToInt32());
    }
}

static class Program
{
    public static AppConfig Cfg;
    public static string BaseDir, CfgPath;
    public static List<FenceForm> Forms = new List<FenceForm>();
    public static bool Exiting;
    static NotifyIcon tray;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppName = "DeskFences";

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "DeskFences_SingleInstance", out created))
        {
            if (!created) return;
            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskFences");
            string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFences");
            Directory.CreateDirectory(BaseDir);
            Directory.CreateDirectory(cfgDir);
            CfgPath = Path.Combine(cfgDir, "config.json");
            Load();

            if (Cfg.Fences.Count == 0) NewFence("Fence 1", false);
            else foreach (var d in Cfg.Fences) ShowFence(d);

            CreateTray();
            WatchDisplays();
            WatchDesktopIcons();
            Application.Run();
            tray.Visible = false;
        }
    }

    static void Load()
    {
        Cfg = null;
        try
        {
            if (File.Exists(CfgPath))
                Cfg = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(CfgPath));
        }
        catch (Exception ex) { MessageBox.Show("Σφάλμα ανάγνωσης ρυθμίσεων: " + ex.Message, AppName); }
        if (Cfg == null || Cfg.Fences == null) Cfg = new AppConfig();

        // Μετάπτωση από την παλιά μορφή: η μοναδική θέση ανήκει στις οθόνες που υπάρχουν τώρα.
        bool migrated = false;
        foreach (var d in Cfg.Fences)
        {
            if (d.Layouts == null) d.Layouts = new Dictionary<string, Rect>();
            if (d.Layouts.Count == 0 && d.W > 0 && d.H > 0)
            {
                d.Layouts[ScreenLayout.Current()] = new Rect { X = d.X, Y = d.Y, W = d.W, H = d.H };
                migrated = true;
            }
            // Θέσεις αποθηκευμένες πριν τη μετάβαση σε per-monitor DPI.
            foreach (var key in d.Layouts.Keys.Where(k => !ScreenLayout.IsCurrentFormat(k)).ToList())
            {
                Rect conv = null;
                try { conv = ScreenLayout.FromLegacy(key, d.Layouts[key]); } catch (FormatException) { }
                d.Layouts.Remove(key);
                string cur = ScreenLayout.Current();
                if (conv != null && !d.Layouts.ContainsKey(cur)) d.Layouts[cur] = conv;
                migrated = true;
            }
        }
        if (migrated) Save();
    }

    static Control invoker;
    static System.Windows.Forms.Timer relayoutTimer;

    // Όταν αλλάζουν οι οθόνες (σύνδεση/αποσύνδεση Remote Desktop, νέα οθόνη, αλλαγή ανάλυσης)
    // τα Windows μετακινούν τα παράθυρα· περιμένουμε να ησυχάσουν και ξαναβάζουμε τη σωστή διάταξη.
    static void WatchDisplays()
    {
        invoker = new Control();
        invoker.CreateControl();
        relayoutTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        relayoutTimer.Tick += (s, e) => { relayoutTimer.Stop(); RelayoutAll(); };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    static void OnDisplayChanged(object sender, EventArgs e) { ScheduleRelayout(); }

    static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.RemoteConnect || e.Reason == SessionSwitchReason.ConsoleConnect
            || e.Reason == SessionSwitchReason.SessionUnlock)
            ScheduleRelayout();
    }

    static void ScheduleRelayout()
    {
        invoker.BeginInvoke((MethodInvoker)(() => { relayoutTimer.Stop(); relayoutTimer.Start(); }));
    }

    public static void RelayoutAll()
    {
        foreach (var f in Forms) f.ApplyLayout();
    }

    static System.Windows.Forms.Timer iconTimer;

    // Κάθε 2 δευτερόλεπτα: όποιο εικονίδιο της επιφάνειας εργασίας βρίσκεται κάτω από fence βγαίνει έξω.
    static void WatchDesktopIcons()
    {
        iconTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        iconTimer.Tick += (s, e) => KeepIconsOut();
        iconTimer.Start();
    }

    static void KeepIconsOut()
    {
        // Όχι όσο ο χρήστης σέρνει κάτι (εικονίδιο ή fence).
        if (Cfg.AllowIconsUnder || Control.MouseButtons != MouseButtons.None) return;
        var rects = Forms.Where(f => f.Visible && f.WindowState == FormWindowState.Normal).Select(f => f.Bounds).ToList();
        DesktopIcons.KeepOutOf(rects, Path.Combine(Path.GetDirectoryName(CfgPath), "icons.log"));
    }

    public static int IconPixels(int dpi)
    {
        int logical = Cfg.IconSize > 0 ? Cfg.IconSize : 32;
        return Math.Min(256, (int)Math.Round(logical * dpi / 96.0));
    }

    public static void SetIconSize(int logical)
    {
        Cfg.IconSize = logical;
        Save();
        foreach (var f in Forms) f.ApplyIconSize();
    }

    public static void Save()
    {
        try
        {
            string tmp = CfgPath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(Cfg));
            if (File.Exists(CfgPath)) File.Replace(tmp, CfgPath, null); else File.Move(tmp, CfgPath);
        }
        catch (Exception ex) { MessageBox.Show("Σφάλμα αποθήκευσης ρυθμίσεων: " + ex.Message, AppName); }
    }

    static void ShowFence(FenceData d)
    {
        var f = new FenceForm(d);
        Forms.Add(f);
        f.Show();
    }

    public static void NewFence(string defaultName, bool ask)
    {
        string name = defaultName;
        if (ask)
        {
            name = Interaction.InputBox("Όνομα του νέου fence:", AppName, defaultName, -1, -1).Trim();
            if (name == "") return;
        }
        string folder = Unique(Path.Combine(BaseDir, SafeName(name)));
        Directory.CreateDirectory(folder);
        int n = Cfg.Fences.Count;
        var d = new FenceData
        {
            Name = name, Folder = folder, Layouts = new Dictionary<string, Rect>(),
            Color = Color.FromArgb(32, 36, 44).ToArgb(), Opacity = 88, Locked = false
        };
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        d.Layouts[ScreenLayout.Current()] = new Rect { X = wa.X + 80 + 40 * (n % 10), Y = wa.Y + 80 + 40 * (n % 10), W = 340, H = 240 };
        Cfg.Fences.Add(d);
        Save();
        ShowFence(d);
    }

    public static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name == "" ? "Fence" : name;
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path);
        bool isDir = Directory.Exists(path);
        string stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public static bool IsUnderBase(string folder)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\'));
        return string.Equals(parent, Path.GetFullPath(BaseDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    public static void Exit()
    {
        Exiting = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Save();
        Application.Exit();
    }

    static void CreateTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Νέο fence…", null, (s, e) => NewFence("Νέο Fence", true));
        menu.Items.Add("Άνοιγμα φακέλου DeskFences", null, (s, e) => Process.Start("explorer.exe", "\"" + BaseDir + "\""));
        var startup = new ToolStripMenuItem("Εκκίνηση με τα Windows") { Checked = IsStartupEnabled() };
        startup.Click += (s, e) => { SetStartup(!startup.Checked); startup.Checked = IsStartupEnabled(); };
        menu.Items.Add(startup);
        var keepOut = new ToolStripMenuItem("Τα εικονίδια μένουν έξω από τα fences") { Checked = !Cfg.AllowIconsUnder };
        keepOut.Click += (s, e) => { Cfg.AllowIconsUnder = keepOut.Checked; Save(); keepOut.Checked = !Cfg.AllowIconsUnder; KeepIconsOut(); };
        menu.Items.Add(keepOut);
        menu.Items.Add("Επαναφορά θέσεων fences", null, (s, e) => RelayoutAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Έξοδος", null, (s, e) => Exit());

        Icon icon = SystemIcons.Application;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        tray = new NotifyIcon { Icon = icon, Text = AppName, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (s, e) => NewFence("Νέο Fence", true);
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }
}

class FenceForm : Form
{
    readonly FenceData D;
    readonly Label title;
    readonly ListView list;
    readonly ImageList imgs;
    readonly System.Windows.Forms.Timer refreshTimer;
    FileSystemWatcher watcher;
    bool draggingOut, allowClose, applyingLayout, inSizeMove;
    // DPI της οθόνης όπου βρίσκεται το fence· όλα τα μεγέθη (τίτλος, κείμενο, άκρες, εικονίδια) κλιμακώνονται με αυτό.
    int dpi = 96, edge = 4;

    int Px(double at96) { return (int)Math.Round(at96 * dpi / 96.0); }

    public FenceForm(FenceData d)
    {
        D = d;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Rectangle bounds = FitOnScreen(ScreenLayout.For(d));
        dpi = Native.DpiFor(bounds);
        Bounds = bounds;
        Opacity = Math.Max(20, Math.Min(100, d.Opacity)) / 100.0;

        int px = Program.IconPixels(dpi);
        imgs = new ImageList { ImageSize = new Size(px, px), ColorDepth = ColorDepth.Depth32Bit };

        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.LargeIcon, BorderStyle = BorderStyle.None,
            LargeImageList = imgs, AllowDrop = true, MultiSelect = true, LabelEdit = true,
            LabelWrap = true, Sorting = SortOrder.None
        };
        list.ItemActivate += (s, e) => OpenSelected();
        list.ItemDrag += List_ItemDrag;
        list.DragEnter += List_DragOver;
        list.DragOver += List_DragOver;
        list.DragDrop += List_DragDrop;
        list.AfterLabelEdit += List_AfterLabelEdit;
        list.MouseUp += List_MouseUp;
        list.KeyDown += List_KeyDown;
        list.HandleCreated += (s, e) => SetIconSpacing();

        title = new Label
        {
            Dock = DockStyle.Top, Text = d.Name, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true
        };
        title.MouseDown += Title_MouseDown;
        title.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) BuildFenceMenu().Show(title, e.Location); };

        Controls.Add(list);
        Controls.Add(title);
        ApplyColor();
        ApplyDpi(dpi);

        refreshTimer = new System.Windows.Forms.Timer { Interval = 300 };
        refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); RefreshItems(); };

        if (!Directory.Exists(D.Folder)) Directory.CreateDirectory(D.Folder);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: όχι στο Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Ιδιοκτήτης το desktop (Progman): το fence μένει ορατό και με Win+D.
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Native.SetOwner(Handle, progman);
        StartWatcher();
        RefreshItems();
    }

    // Τα μεγέθη ορίζονται σε pixels για το DPI της οθόνης (η WinForms του .NET Framework δεν το κάνει μόνη της).
    void ApplyDpi(int newDpi)
    {
        dpi = newDpi;
        edge = Px(4);
        Padding = new Padding(Math.Max(1, edge - 2));
        MinimumSize = new Size(Px(80), Px(46));
        Font = new Font("Segoe UI", 9f * dpi / 72f, GraphicsUnit.Pixel);
        title.Font = new Font("Segoe UI Semibold", 10f * dpi / 72f, GraphicsUnit.Pixel);
        title.Height = Px(17);
        ApplyIconSize();
    }

    void SyncDpi()
    {
        int d = Native.DpiFor(Bounds);
        if (d != dpi) ApplyDpi(d);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_ENTERSIZEMOVE) inSizeMove = true;
        else if (m.Msg == Native.WM_EXITSIZEMOVE) inSizeMove = false;
        else if (m.Msg == Native.WM_DPICHANGED)
        {
            // Όταν ο χρήστης σέρνει το fence σε οθόνη με άλλη κλίμακα, δεχόμαστε το μέγεθος που προτείνουν τα Windows.
            // Όταν το τοποθετούμε εμείς (επαναφορά διάταξης), κρατάμε το αποθηκευμένο μέγεθος.
            if (inSizeMove)
            {
                var rc = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Native.SetWindowPos(Handle, IntPtr.Zero, rc.L, rc.T, rc.R - rc.L, rc.B - rc.T,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            // Το DPI του μηνύματος δεν είναι πάντα σωστό για παράθυρα του desktop· μετράμε την οθόνη όπου βρίσκεται.
            SyncDpi();
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == Native.WM_WINDOWPOSCHANGING)
        {
            // Πάντα στο κάτω μέρος της στοίβας παραθύρων, σαν μέρος του desktop.
            var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
            wp.hwndInsertAfter = Native.HWND_BOTTOM;
            wp.flags &= ~Native.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        base.WndProc(ref m);
        if (m.Msg == Native.WM_NCHITTEST && !D.Locked && (int)m.Result == Native.HTCLIENT)
        {
            long lp = m.LParam.ToInt64();
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int e = Math.Max(4, edge + 3);
            bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
            if (t && l) m.Result = (IntPtr)Native.HTTOPLEFT;
            else if (t && r) m.Result = (IntPtr)Native.HTTOPRIGHT;
            else if (b && l) m.Result = (IntPtr)Native.HTBOTTOMLEFT;
            else if (b && r) m.Result = (IntPtr)Native.HTBOTTOMRIGHT;
            else if (l) m.Result = (IntPtr)Native.HTLEFT;
            else if (r) m.Result = (IntPtr)Native.HTRIGHT;
            else if (t) m.Result = (IntPtr)Native.HTTOP;
            else if (b) m.Result = (IntPtr)Native.HTBOTTOM;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        base.OnResize(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SyncDpi();
        SaveBounds();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SyncDpi();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowClose && !Program.Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    static Rectangle FitOnScreen(Rectangle r)
    {
        foreach (var s in Screen.AllScreens)
            if (s.WorkingArea.IntersectsWith(r)) return r;
        return new Rectangle(60, 60, r.Width, r.Height);
    }

    // Αποθηκεύεται μόνο όταν ο χρήστης μετακινεί/αλλάζει μέγεθος, και μόνο για τον τρέχοντα συνδυασμό οθονών.
    void SaveBounds()
    {
        if (applyingLayout) return;
        D.Layouts[ScreenLayout.Current()] = ScreenLayout.FromRectangle(Bounds);
        Program.Save();
    }

    public void ApplyLayout()
    {
        applyingLayout = true;
        try { Bounds = FitOnScreen(ScreenLayout.For(D)); SyncDpi(); }
        finally { applyingLayout = false; }
    }

    void ApplyColor()
    {
        Color c = Color.FromArgb(255, Color.FromArgb(D.Color));
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        Color fore = lum > 0.6 ? Color.Black : Color.White;
        int delta = lum < 0.5 ? 22 : -22;
        Color head = Color.FromArgb(Clamp(c.R + delta), Clamp(c.G + delta), Clamp(c.B + delta));
        BackColor = head;
        title.BackColor = head; title.ForeColor = fore;
        list.BackColor = c; list.ForeColor = fore;
    }

    static int Clamp(int v) { return Math.Max(0, Math.Min(255, v)); }

    // ---------- Περιεχόμενα ----------

    void StartWatcher()
    {
        if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        if (!Directory.Exists(D.Folder)) return;
        watcher = new FileSystemWatcher(D.Folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = false, SynchronizingObject = this
        };
        FileSystemEventHandler h = (s, e) => { refreshTimer.Stop(); refreshTimer.Start(); };
        watcher.Created += h; watcher.Deleted += h;
        watcher.Renamed += (s, e) => { refreshTimer.Stop(); refreshTimer.Start(); };
        watcher.EnableRaisingEvents = true;
    }

    void RefreshItems()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (Image img in imgs.Images) img.Dispose();
        imgs.Images.Clear();
        if (Directory.Exists(D.Folder))
        {
            var entries = new DirectoryInfo(D.Folder).GetFileSystemInfos()
                .Where(fi => (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(fi => fi is DirectoryInfo ? 0 : 1)
                .ThenBy(fi => fi.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var fi in entries)
            {
                imgs.Images.Add(GetIcon(fi.FullName, imgs.ImageSize.Width));
                string text = fi is DirectoryInfo ? fi.Name : Path.GetFileNameWithoutExtension(fi.Name);
                if (text == "") text = fi.Name;
                list.Items.Add(new ListViewItem(text, imgs.Images.Count - 1) { Tag = fi.FullName, ToolTipText = fi.Name });
            }
        }
        list.EndUpdate();
    }

    public void ApplyIconSize()
    {
        int px = Program.IconPixels(dpi);
        if (imgs.ImageSize.Width != px)
        {
            list.LargeImageList = null;
            foreach (Image img in imgs.Images) img.Dispose();
            imgs.Images.Clear();
            imgs.ImageSize = new Size(px, px);
            list.LargeImageList = imgs;
        }
        SetIconSpacing();
        RefreshItems();
    }

    // Απόσταση εικονιδίων ανάλογη του μεγέθους τους (η προεπιλογή ακολουθεί την κύρια οθόνη και δεν ταιριάζει παντού).
    void SetIconSpacing()
    {
        if (!list.IsHandleCreated) return;
        int px = imgs.ImageSize.Width;
        int cx = Math.Max(px + Px(28), Px(76)), cy = px + Px(36);
        Native.SendMessage(list.Handle, Native.LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF)));
    }

    // Για μεγάλα μεγέθη παίρνουμε το εικονίδιο από τη λίστα "extra large" του shell (καθαρό, όχι μεγεθυμένο).
    static Bitmap GetIcon(string path, int px)
    {
        if (px > SystemInformation.IconSize.Width)
        {
            var bmp = GetShellIcon(path, Native.SHIL_EXTRALARGE);
            if (bmp != null) return bmp;
        }
        return GetLargeIcon(path);
    }

    static Bitmap GetShellIcon(string path, int list)
    {
        var info = new Native.SHFILEINFO();
        if (Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
                Native.SHGFI_SYSICONINDEX | Native.SHGFI_ICON | Native.SHGFI_OVERLAYINDEX) == IntPtr.Zero) return null;
        if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon);
        IntPtr himl;
        Guid iid = Native.IID_IImageList;
        if (Native.SHGetImageList(list, ref iid, out himl) != 0 || himl == IntPtr.Zero) return null;
        int index = info.iIcon & 0xFFFFFF, overlay = (info.iIcon >> 24) & 0xFF;
        IntPtr h = Native.ImageList_GetIcon(himl, index, Native.ILD_TRANSPARENT | (overlay << 8));
        if (h == IntPtr.Zero) return null;
        try { using (var ic = Icon.FromHandle(h)) return ic.ToBitmap(); }
        finally { Native.DestroyIcon(h); }
    }

    static Bitmap GetLargeIcon(string path)
    {
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info),
            Native.SHGFI_ICON | Native.SHGFI_LARGEICON | Native.SHGFI_ADDOVERLAYS);
        if (info.hIcon == IntPtr.Zero) return SystemIcons.WinLogo.ToBitmap();
        try { using (var ic = Icon.FromHandle(info.hIcon)) return ic.ToBitmap(); }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    List<string> SelectedPaths()
    {
        return list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
    }

    void OpenSelected()
    {
        foreach (var p in SelectedPaths())
        {
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true, WorkingDirectory = D.Folder }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "DeskFences"); }
        }
    }

    // ---------- Drag & drop ----------

    void List_ItemDrag(object sender, ItemDragEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        draggingOut = true;
        try
        {
            list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()),
                DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally { draggingOut = false; }
        refreshTimer.Stop(); refreshTimer.Start();
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        if (draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.None; return; }
        bool ctrl = (e.KeyState & 8) != 0;
        e.Effect = ctrl && (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (e.AllowedEffect & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                 : (e.AllowedEffect & DragDropEffects.Copy);
    }

    void List_DragDrop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null) return;
        ImportPaths(paths, e.Effect == DragDropEffects.Copy);
    }

    void ImportPaths(string[] paths, bool copy)
    {
        string folder = Path.GetFullPath(D.Folder).TrimEnd('\\');
        foreach (var p in paths)
        {
            string name = Path.GetFileName(p.TrimEnd('\\'));
            if (name == "") continue;
            string parent = Path.GetDirectoryName(Path.GetFullPath(p).TrimEnd('\\'));
            if (string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase)) continue;
            if (folder.StartsWith(Path.GetFullPath(p).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder, Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            string dest = Program.Unique(Path.Combine(folder, name));
            try
            {
                if (Directory.Exists(p))
                {
                    if (copy) VBFS.CopyDirectory(p, dest, UIOption.OnlyErrorDialogs);
                    else VBFS.MoveDirectory(p, dest, UIOption.OnlyErrorDialogs);
                }
                else if (File.Exists(p))
                {
                    if (copy) VBFS.CopyFile(p, dest, UIOption.OnlyErrorDialogs);
                    else VBFS.MoveFile(p, dest, UIOption.OnlyErrorDialogs);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show("Δεν ήταν δυνατή η μεταφορά του \"" + name + "\":\n" + ex.Message, "DeskFences"); }
        }
        RefreshItems();
    }

    void MoveToDesktop(IEnumerable<string> paths)
    {
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var p in paths)
        {
            string dest = Program.Unique(Path.Combine(desk, Path.GetFileName(p)));
            try
            {
                if (Directory.Exists(p)) VBFS.MoveDirectory(p, dest, UIOption.OnlyErrorDialogs);
                else if (File.Exists(p)) VBFS.MoveFile(p, dest, UIOption.OnlyErrorDialogs);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show(ex.Message, "DeskFences"); }
        }
        RefreshItems();
    }

    void DeleteSelected()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) VBFS.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else VBFS.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show(ex.Message, "DeskFences"); }
        }
        RefreshItems();
    }

    void List_AfterLabelEdit(object sender, LabelEditEventArgs e)
    {
        e.CancelEdit = true;
        if (string.IsNullOrWhiteSpace(e.Label)) return;
        string oldPath = (string)list.Items[e.Item].Tag;
        bool isDir = Directory.Exists(oldPath);
        string ext = isDir ? "" : Path.GetExtension(oldPath);
        string newName = e.Label.Trim();
        if (ext != "" && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("Το όνομα περιέχει μη επιτρεπτούς χαρακτήρες.", "DeskFences");
            return;
        }
        string newPath = Path.Combine(D.Folder, newName);
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
        try
        {
            if (isDir) VBFS.RenameDirectory(oldPath, newName);
            else VBFS.RenameFile(oldPath, newName);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "DeskFences"); }
        RefreshItems();
    }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) DeleteSelected();
        else if (e.KeyCode == Keys.F2 && list.SelectedItems.Count == 1) list.SelectedItems[0].BeginEdit();
        else if (e.KeyCode == Keys.F5) RefreshItems();
        else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in list.Items) i.Selected = true;
    }

    void List_MouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = list.HitTest(e.Location).Item;
        if (hit == null) { BuildFenceMenu().Show(list, e.Location); return; }
        if (!hit.Selected) { list.SelectedItems.Clear(); hit.Selected = true; }

        var m = new ContextMenuStrip();
        m.Items.Add("Άνοιγμα", null, (s, a) => OpenSelected());
        m.Items.Add("Εμφάνιση στον Explorer", null, (s, a) =>
            Process.Start("explorer.exe", "/select,\"" + (string)hit.Tag + "\""));
        m.Items.Add("Μετονομασία", null, (s, a) => hit.BeginEdit()).Enabled = list.SelectedItems.Count == 1;
        m.Items.Add("Επιστροφή στην επιφάνεια εργασίας", null, (s, a) => MoveToDesktop(SelectedPaths()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Διαγραφή (στον Κάδο)", null, (s, a) => DeleteSelected());
        m.Show(list, e.Location);
    }

    // ---------- Fence ----------

    void Title_MouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { RenameFence(); return; }
        if (D.Locked) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
    }

    ContextMenuStrip BuildFenceMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Μετονομασία fence…", null, (s, e) => RenameFence());
        m.Items.Add("Νέο fence…", null, (s, e) => Program.NewFence("Νέο Fence", true));
        m.Items.Add("Άνοιγμα φακέλου", null, (s, e) => Process.Start("explorer.exe", "\"" + D.Folder + "\""));
        m.Items.Add("Ανανέωση", null, (s, e) => RefreshItems());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Χρώμα…", null, (s, e) => PickColor());
        var op = new ToolStripMenuItem("Διαφάνεια");
        foreach (int v in new[] { 100, 90, 80, 70, 60, 50, 40 })
        {
            int val = v;
            var it = new ToolStripMenuItem(v + "%") { Checked = D.Opacity == v };
            it.Click += (s, e) => { D.Opacity = val; Opacity = val / 100.0; Program.Save(); };
            op.DropDownItems.Add(it);
        }
        m.Items.Add(op);
        var sz = new ToolStripMenuItem("Μέγεθος εικονιδίων (όλα τα fences)");
        int current = Program.Cfg.IconSize > 0 ? Program.Cfg.IconSize : 32;
        foreach (var opt in new[] { Tuple.Create("Μικρά", 24), Tuple.Create("Μεσαία", 32), Tuple.Create("Μεγάλα", 48) })
        {
            int val = opt.Item2;
            var it = new ToolStripMenuItem(opt.Item1) { Checked = current == val };
            it.Click += (s, e) => Program.SetIconSize(val);
            sz.DropDownItems.Add(it);
        }
        m.Items.Add(sz);
        var lk = new ToolStripMenuItem("Κλείδωμα θέσης & μεγέθους") { Checked = D.Locked };
        lk.Click += (s, e) => { D.Locked = !D.Locked; Program.Save(); };
        m.Items.Add(lk);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Διαγραφή fence…", null, (s, e) => DeleteFence());
        m.Items.Add("Έξοδος από DeskFences", null, (s, e) => Program.Exit());
        return m;
    }

    void PickColor()
    {
        using (var dlg = new ColorDialog { Color = Color.FromArgb(D.Color), FullOpen = true })
        {
            if (dlg.ShowDialog() != DialogResult.OK) return;
            D.Color = dlg.Color.ToArgb();
            ApplyColor();
            Program.Save();
        }
    }

    void RenameFence()
    {
        string n = Interaction.InputBox("Νέο όνομα για το fence:", "Μετονομασία", D.Name, -1, -1).Trim();
        if (n == "" || n == D.Name) return;

        // Αν ο φάκελος είναι δικός μας (μέσα στο DeskFences), μετονομάζεται κι αυτός.
        if (Program.IsUnderBase(D.Folder) && Directory.Exists(D.Folder))
        {
            string target = Path.Combine(Program.BaseDir, Program.SafeName(n));
            if (!string.Equals(target, D.Folder, StringComparison.Ordinal))
            {
                if (watcher != null) watcher.EnableRaisingEvents = false;
                try
                {
                    if (string.Equals(target, D.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        string tmp = D.Folder + ".renaming";
                        Directory.Move(D.Folder, tmp);
                        Directory.Move(tmp, target);
                    }
                    else
                    {
                        target = Program.Unique(target);
                        Directory.Move(D.Folder, target);
                    }
                    D.Folder = target;
                }
                catch (Exception ex) { MessageBox.Show("Ο φάκελος δεν μετονομάστηκε: " + ex.Message, "DeskFences"); }
                StartWatcher();
                RefreshItems();
            }
        }
        D.Name = n;
        title.Text = n;
        Program.Save();
    }

    void DeleteFence()
    {
        var r = MessageBox.Show("Να διαγραφεί το fence \"" + D.Name + "\";\n\nΤα αρχεία του θα επιστρέψουν στην επιφάνεια εργασίας.",
            "DeskFences", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        if (watcher != null) watcher.EnableRaisingEvents = false;
        if (Directory.Exists(D.Folder))
        {
            MoveToDesktop(Directory.GetFileSystemEntries(D.Folder)
                .Where(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0).ToList());
            try
            {
                if (Program.IsUnderBase(D.Folder) && !Directory.EnumerateFileSystemEntries(D.Folder)
                        .Any(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0))
                    Directory.Delete(D.Folder, true);
            }
            catch { }
        }
        Program.Cfg.Fences.Remove(D);
        Program.Forms.Remove(this);
        Program.Save();
        allowClose = true;
        Close();
    }
}
