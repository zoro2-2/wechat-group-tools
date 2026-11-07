using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 群好友助手 —— 微信 4.x 群成员读取工具
// 单文件 WinForms 应用；必须以管理员身份运行（读取 Weixin.exe 内存需要同/高完整性级别）
namespace GroupHelper
{
    // ============================ 数据模型 ============================
    public class Member
    {
        public string Group = "";
        public string Wxid = "";
        public string Nick = "";
        public bool Checked = false;
        public bool InTarget = false;   // 是否属于本次识别出的"当前群"
    }

    // 下拉框条目：群号 + 人数
    public class GroupItem
    {
        public string Room = "";
        public int Count = 0;
        public bool IsJustLoaded = false;
        public override string ToString()
        {
            string shown = Room;
            string label;
            if (Config.Labels.TryGetValue(Room, out label) && label.Length > 0)
                shown = label + "  ·  " + Room;
            return shown + "   (≈" + Count + " 人 · 估算)" + (IsJustLoaded ? "  ← 本次新加载" : "");
        }
    }

    // ============================ 配置 ============================
    public static class Config
    {
        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "群好友助手");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static int DailyLimit = 50;
        public static int IntervalMin = 30;
        public static int IntervalMax = 90;
        public static int CooldownHours = 6;

        static string IniPath { get { return Path.Combine(DataDir, "settings.ini"); } }
        static string LabelPath { get { return Path.Combine(DataDir, "group-labels.tsv"); } }

        // 群号 -> 用户自定义群名
        public static Dictionary<string, string> Labels = new Dictionary<string, string>();

        public static void LoadLabels()
        {
            try
            {
                Labels.Clear();
                if (!File.Exists(LabelPath)) return;
                foreach (var line in File.ReadAllLines(LabelPath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 2 && p[0].Length > 0) Labels[p[0]] = p[1];
                }
            }
            catch { }
        }

        public static void SaveLabels()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in Labels) sb.AppendLine(kv.Key + "\t" + kv.Value);
                File.WriteAllText(LabelPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        public static string LabelOf(string room)
        {
            string v;
            if (Labels.TryGetValue(room, out v) && v.Length > 0) return v;
            return room;
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (var line in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    var p = line.Split('=');
                    if (p.Length != 2) continue;
                    int v;
                    if (!int.TryParse(p[1], out v)) continue;
                    switch (p[0].Trim())
                    {
                        case "DailyLimit": DailyLimit = v; break;
                        case "IntervalMin": IntervalMin = v; break;
                        case "IntervalMax": IntervalMax = v; break;
                        case "CooldownHours": CooldownHours = v; break;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(IniPath, new string[]
                {
                    "DailyLimit=" + DailyLimit,
                    "IntervalMin=" + IntervalMin,
                    "IntervalMax=" + IntervalMax,
                    "CooldownHours=" + CooldownHours
                }, Encoding.UTF8);
            }
            catch { }
        }
    }

    // ============================ Win32 ============================
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint a, bool i, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI m, IntPtr len);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inp, int cb);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

        public delegate bool EnumProc(IntPtr h, IntPtr lp);
        [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr Base, Alloc; public uint A1, A2; public IntPtr Size; public uint State, Protect, Type, A3; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public int W { get { return R - L; } } public int H { get { return B - T; } } }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct IU { [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public IU u; }

        public const uint ME_WHEEL = 0x0800;
    }

    // ============================ 读取引擎 ============================
    public class WxReader
    {
        const uint PVMR = 0x0010, PQI = 0x0400, MEM_COMMIT = 0x1000;
        const int PS = 4096;

        byte[] _buf = new byte[2 << 20];
        long _bufBase;
        HashSet<string> _accum = new HashSet<string>();
        HashSet<string> _pageHdr = new HashSet<string>();

        public int RowCount { get { return _accum.Count; } }
        public List<string> Log = new List<string>();

        // 读取结果：按群汇总 + 推荐的"当前群"
        public Dictionary<string, int> RoomCounts = new Dictionary<string, int>();
        public string SuggestedRoom = "";
        public int SuggestedCountFromNew = 0;   // 该群在"本次新加载页"里的成员数

        HashSet<string> _cachedPages = new HashSet<string>();
        HashSet<string> _newPageAddrs = new HashSet<string>();

        void L(string s, Action<string> report)
        {
            Log.Add(s);
            if (report != null) report(s);
        }

        public static int FindWeixin(out uint pid)
        {
            uint found = 0; int hwnd = -1;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (!Native.IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256); Native.GetWindowText(h, sb, sb.Capacity);
                var cw = new StringBuilder(256); Native.GetClassName(h, cw, cw.Capacity);
                if (cw.ToString().Contains("QWindowIcon") && !cw.ToString().Contains("SubWindow"))
                {
                    uint p; Native.GetWindowThreadProcessId(h, out p);
                    found = p; hwnd = (int)h.ToInt64(); return false;
                }
                return true;
            }, IntPtr.Zero);
            pid = found;
            return hwnd;
        }

        int U8(long a) { return _buf[(int)(a - _bufBase)]; }
        int W16(long a) { return (U8(a) << 8) | U8(a + 1); }

        long Varint(long o, out int nb)
        {
            long v = 0; int i = 0;
            for (; i < 9; i++)
            {
                byte b = (byte)U8(o + i);
                v = (v << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0) { i++; break; }
            }
            nb = i; return v;
        }

        int SerSize(long st)
        {
            if (st == 0) return 0;
            if (st >= 1 && st <= 4) return (int)st;
            if (st == 5) return 6;
            if (st == 6 || st == 7) return 8;
            if (st == 8 || st == 9) return 0;
            if (st >= 12) return (int)((st - 12) / 2);
            return -1;
        }

        class Rg { public long Base, Size; }

        List<Rg> Regions(IntPtr h)
        {
            var l = new List<Rg>(); long a = 0; var m = new Native.MBI(); int g = 0;
            while (g++ < 400000)
            {
                if (Native.VirtualQueryEx(h, (IntPtr)a, out m, (IntPtr)Marshal.SizeOf(typeof(Native.MBI))) == IntPtr.Zero) break;
                long s = m.Size.ToInt64(); if (s <= 0) break;
                if (m.State == MEM_COMMIT && (m.Protect == 2 || m.Protect == 4 || m.Protect == 0x20 || m.Protect == 0x40 || m.Protect == 0x80))
                    l.Add(new Rg { Base = m.Base.ToInt64(), Size = s });
                a += s; if (a < 0) break;
            }
            return l;
        }

        bool ValidPage(long off)
        {
            int tp = U8(off);
            if (tp != 0x0D && tp != 0x0A) return false;
            int cells = W16(off + 3);
            int cs = W16(off + 5); if (cs == 0) cs = 65536;
            int hdrEnd = 8 + cells * 2;
            if (cells <= 0 || cells > 2000 || hdrEnd > PS || cs < hdrEnd || cs > PS) return false;
            for (int i = 0; i < cells; i++) { int cp = W16(off + 8 + i * 2); if (cp < cs || cp >= PS) return false; }
            return true;
        }

        void Carve(long off, int type)
        {
            int cells = W16(off + 3);
            string addrKey = off.ToString("X");
            if (!_cachedPages.Contains(addrKey)) _newPageAddrs.Add(addrKey);
            _pageHdr.Add("P\t" + addrKey + "\t" + type.ToString("X2") + "\t" + cells);
            for (int i = 0; i < cells; i++)
            {
                int cp = W16(off + 8 + i * 2);
                long p = off + cp;
                int n1, n2;
                long payLen = Varint(p, out n1);
                long rowid = 0;
                long payload;
                if (type == 0x0A) { payload = p + n1; }
                else { rowid = Varint(p + n1, out n2); payload = p + n1 + n2; }
                if (payLen <= 0 || payLen > PS - 100) continue;
                if (payload + payLen > off + PS) continue;
                int nb; long hdrLen = Varint(payload, out nb);
                if (hdrLen <= 0 || hdrLen > payLen) continue;
                long hp = payload + nb, hend = payload + (int)hdrLen, dp = hend;
                var sb = new StringBuilder();
                bool bad = false;
                while (hp < hend)
                {
                    long st = Varint(hp, out nb); hp += nb;
                    int sz = SerSize(st);
                    if (sz < 0) { bad = true; break; }
                    if (dp + sz > payload + payLen) { bad = true; break; }
                    if (sb.Length > 0) sb.Append('\t');
                    if (st >= 12)
                    {
                        var bytes = new byte[sz];
                        for (int k = 0; k < sz; k++) bytes[k] = (byte)U8(dp + k);
                        string v = Encoding.UTF8.GetString(bytes).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
                        if (v.Length > 2000) v = v.Substring(0, 2000);
                        sb.Append(v);
                    }
                    else if (st >= 1 && st <= 6)
                    {
                        long v = 0; for (int k = 0; k < sz; k++) v = (v << 8) | (byte)U8(dp + k);
                        sb.Append(v);
                    }
                    else sb.Append("");
                    dp += sz;
                }
                if (bad) continue;
                _accum.Add("R\t" + off.ToString("X") + "\t" + rowid + "\t" + sb.ToString());
            }
        }

        void ScanOnce(IntPtr h)
        {
            foreach (var r in Regions(h))
            {
                long off = 0;
                while (off < r.Size)
                {
                    int want = (int)Math.Min(_buf.Length, r.Size - off);
                    IntPtr got;
                    if (!Native.ReadProcessMemory(h, (IntPtr)(r.Base + off), _buf, (IntPtr)want, out got) || got.ToInt64() <= 0) { off += 0x10000; continue; }
                    int n = (int)got.ToInt64();
                    _bufBase = r.Base + off;
                    for (int i = 0; i + PS <= n; i++)
                    {
                        int t = _buf[i];
                        if (t != 0x0D && t != 0x0A) continue;
                        if (!ValidPage(_bufBase + i)) continue;
                        Carve(_bufBase + i, t);
                    }
                    off += n;
                }
            }
        }

        // ---------- 质量校验：整页形状一致才采信 ----------
        // 映射表：同一 id 在不同数据库里行号空间互相独立，不能"首次命中即采用"。
        // 做法：统计每个 id 的所有候选值，只接受"众数占比 >= 70% 且出现过 >= 2 次"的稳定映射。
        // 映射表：只采纳"整页形状一致且为 T / Ti"的页（避免跨库串号），每个 id 取众数
        Dictionary<long, string> Name2Id(HashSet<string> onlyPages)
        {
            // 第一遍：找出形状一致的页，并记录其形状
            var pageShape = new Dictionary<string, string>();
            var pageRows = new Dictionary<string, List<string[]>>();
            foreach (var line in _accum)
            {
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                if (onlyPages != null && !onlyPages.Contains(p[1])) continue;
                if (!pageRows.ContainsKey(p[1])) { pageRows[p[1]] = new List<string[]>(); pageShape[p[1]] = null; }
                var cols = new string[p.Length - 2];
                Array.Copy(p, 2, cols, 0, cols.Length);   // [rowid, col0, col1...]
                pageRows[p[1]].Add(cols);
            }

            foreach (var kv in pageRows)
            {
                var rows = kv.Value;
                if (rows.Count == 0) continue;
                string sh = ShapeCols(rows[0]);
                if (sh != "T" && sh != "Ti") { pageShape[kv.Key] = null; continue; }
                bool uniform = true;
                foreach (var r in rows) if (ShapeCols(r) != sh) { uniform = false; break; }
                pageShape[kv.Key] = uniform ? sh : null;
            }

            // 第二遍：投票
            var votes = new Dictionary<long, Dictionary<string, int>>();
            foreach (var kv in pageRows)
            {
                string sh = pageShape[kv.Key];
                if (sh == null) continue;
                foreach (var r in kv.Value)
                {
                    long id;
                    if (!long.TryParse(r[0], out id)) continue;
                    string val = null;
                    if (sh == "T") { if (r.Length > 1) val = r[1]; if (id <= 0) continue; long rid = id; id = rid; }
                    else { if (r.Length > 2) val = r[1]; long t; if (!long.TryParse(r[2], out t)) continue; id = t; }
                    if (val == null || !LooksId(val)) continue;
                    AddVote(votes, id, val);
                }
            }

            var map = new Dictionary<long, string>();
            foreach (var kv in votes)
            {
                int bc = 0; string best = null;
                foreach (var v in kv.Value) if (v.Value > bc) { bc = v.Value; best = v.Key; }
                if (best != null) map[kv.Key] = best;
            }
            return map;
        }
        static void AddVote(Dictionary<long, Dictionary<string, int>> votes, long id, string val)
        {
            if (!votes.ContainsKey(id)) votes[id] = new Dictionary<string, int>();
            var m = votes[id];
            if (!m.ContainsKey(val)) m[val] = 0;
            m[val]++;
        }
        static string ShapeCols(string[] rowWithRowid)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < rowWithRowid.Length; i++)
            {
                string c = rowWithRowid[i];
                long v;
                if (string.IsNullOrEmpty(c)) sb.Append('n');
                else if (long.TryParse(c, out v)) sb.Append('i');
                else sb.Append('T');
            }
            return sb.ToString();
        }

        List<long[]> Pairs(HashSet<string> onlyPages, Dictionary<long, string> n2id)
        {
            var byPage = new Dictionary<string, List<string[]>>();
            foreach (var line in _accum)
            {
                var p = line.Split('\t');
                if (p.Length != 5) continue;   // R addr rowid col0 col1
                if (onlyPages != null && !onlyPages.Contains(p[1])) continue;
                if (!byPage.ContainsKey(p[1])) byPage[p[1]] = new List<string[]>();
                byPage[p[1]].Add(new string[] { p[3], p[4] });
            }
            var list = new List<long[]>();
            foreach (var kv in byPage)
            {
                var rows = kv.Value;
                if (rows.Count < 8) continue;                 // 页太小不可信
                bool uniform = true;
                foreach (var r in rows) if (Shape(r) != "ii") { uniform = false; break; }
                if (!uniform) continue;

                // chatroom_member 按 (room_id, member_id) 排序，一页里应绝大多数是同一个 room
                var cnt = new Dictionary<string, int>();
                foreach (var r in rows) { if (!cnt.ContainsKey(r[0])) cnt[r[0]] = 0; cnt[r[0]]++; }
                var dom = cnt.OrderByDescending(c => c.Value).First();
                if ((double)dom.Value / rows.Count < 0.70) continue;   // 一页散落多个 room -> 不是成员表

                long domId;
                if (!long.TryParse(dom.Key, out domId)) continue;
                string domName;
                if (!n2id.TryGetValue(domId, out domName)) continue;              // 主导群必须能映射
                if (domName.IndexOf("@chatroom", StringComparison.Ordinal) < 0) continue;  // 且必须是群

                foreach (var r in rows)
                {
                    long a, b;
                    if (long.TryParse(r[0], out a) && long.TryParse(r[1], out b) && a > 0 && b > 0)
                        list.Add(new long[] { a, b });
                }
            }
            return list;
        }

        static string Shape(string[] cols)
        {
            var sb = new StringBuilder();
            foreach (var c in cols)
            {
                long v;
                if (string.IsNullOrEmpty(c)) sb.Append('n');
                else if (long.TryParse(c, out v)) sb.Append('i');
                else sb.Append('T');
            }
            return sb.ToString();
        }

        static bool LooksId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
            if (s.StartsWith("wxid_")) return true;
            if (s.StartsWith("gh_")) return true;
            if (s.EndsWith("@chatroom") || s.EndsWith("@openim")) return true;
            return false;
        }

        static bool HasCjk(string s)
        {
            foreach (char c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
            return false;
        }

        static readonly string[] BadNames = {
            "我通过了","朋友验证","开始聊天","撤回","红包","转账","拍了拍","加入群聊",
            "以下为","微信运动","邀请你","已过期","表情","图片","语音","视频","文件",
            "位置","名片","小程序","创建了群聊"
        };

        static bool PlausibleName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length < 1 || s.Length > 24) return false;
            if (s.StartsWith("wxid_") || s.StartsWith("gh_")) return false;
            if (s.IndexOf("@chatroom", StringComparison.Ordinal) >= 0) return false;
            if (s.IndexOf("@openim", StringComparison.Ordinal) >= 0) return false;

            // 纯数字（时间戳/消息 id）不是名字
            bool allDigit = true;
            foreach (char c in s) if (c < '0' || c > '9') { allDigit = false; break; }
            if (allDigit) return false;

            // 必须含至少一个字母或汉字
            bool hasLetter = false;
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= 0x4E00 && c <= 0x9FFF)) { hasLetter = true; break; }
            }
            if (!hasLetter) return false;

            // 排除系统话术
            foreach (var b in BadNames) if (s.Contains(b)) return false;

            // 排除像路径/句子的内容
            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0) return false;
            if (s.Length > 14 && (s.IndexOf('，') >= 0 || s.IndexOf(',') >= 0 || s.IndexOf('。') >= 0)) return false;
            return true;
        }

        // 群号 -> 群名称（来自内存里的 "用户名 -> 显示名" 行）
        public static Dictionary<string, string> GroupNames = new Dictionary<string, string>();

        Dictionary<string, string> Nicknames()
        {
            var votes = new Dictionary<string, Dictionary<string, int>>();
            var groupVotes = new Dictionary<string, Dictionary<string, int>>();
            foreach (var line in _accum)
            {
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                for (int i = 3; i < p.Length - 1; i++)
                {
                    string key = p[i];
                    bool isUser = key.StartsWith("wxid_");
                    bool isGroup = key.EndsWith("@chatroom") || key.EndsWith("@openim");
                    if (!isUser && !isGroup) continue;
                    if (!PlausibleName(p[i + 1])) continue;

                    var target = isUser ? votes : groupVotes;
                    if (!target.ContainsKey(key)) target[key] = new Dictionary<string, int>();
                    var m = target[key];
                    if (!m.ContainsKey(p[i + 1])) m[p[i + 1]] = 0;
                    m[p[i + 1]]++;
                    break;
                }
            }

            // 群名：取票数最高的
            GroupNames.Clear();
            foreach (var kv in groupVotes)
            {
                string best = null; int bc = -1;
                foreach (var c in kv.Value)
                    if (c.Value > bc || (c.Value == bc && best != null && c.Key.Length < best.Length)) { best = c.Key; bc = c.Value; }
                if (best != null) GroupNames[kv.Key] = best;
            }
            var res = new Dictionary<string, string>();
            foreach (var kv in votes)
            {
                string best = null; int bc = -1;
                foreach (var c in kv.Value)
                {
                    // 票数多者优先；票数相同取更短的（昵称通常短于聊天内容）
                    if (c.Value > bc || (c.Value == bc && best != null && c.Key.Length < best.Length))
                    { best = c.Key; bc = c.Value; }
                }
                if (best != null) res[kv.Key] = best;
            }
            return res;
        }

        static string CachePath { get { return Path.Combine(Config.DataDir, "pages-cache.tsv"); } }

        void LoadCache(Action<string> report)
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                int n = 0;
                using (var sr = new StreamReader(CachePath, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("R\t")) { if (_accum.Add(line)) n++; }
                        else if (line.StartsWith("P\t")) { _pageHdr.Add(line); var q = line.Split('\t'); if (q.Length > 1) _cachedPages.Add(q[1]); }
                    }
                }
                if (n > 0) L("已载入历史缓存 " + n + " 行", report);
            }
            catch { }
        }

        void SaveCache(Action<string> report)
        {
            try
            {
                using (var sw = new StreamWriter(CachePath, false, Encoding.UTF8))
                {
                    foreach (var p in _pageHdr) sw.WriteLine(p);
                    foreach (var r in _accum) sw.WriteLine(r);
                }
                L("已保存累加缓存（" + _accum.Count + " 行），下次读取会更快更全", report);
            }
            catch { }
        }

        /// <summary>读取：预热 UI + 多轮采样 + 严格拼接</summary>
        public List<Member> Read(int seconds, Action<string> report)
        {
            LoadCache(report);
            uint pid;
            int hwnd = FindWeixin(out pid);
            if (hwnd < 0) { L("未找到微信窗口，请先启动并登录微信", report); return null; }

            IntPtr h = Native.OpenProcess(PVMR | PQI, false, (int)pid);
            if (h == IntPtr.Zero)
            {
                L("无法读取微信进程（权限不足）。请以管理员身份运行本程序。", report);
                return null;
            }

            try
            {
                Native.RECT wr;
                Native.GetWindowRect((IntPtr)hwnd, out wr);
                if (wr.W < 400 || wr.H < 400 || wr.L <= -10000)
                {
                    L("微信窗口处于最小化，自动还原…", report);
                    Native.PostMessage((IntPtr)hwnd, 0x0112, (IntPtr)0xF120, IntPtr.Zero);
                    Thread.Sleep(900);
                    Native.GetWindowRect((IntPtr)hwnd, out wr);
                }
                L("微信进程 " + pid + "，窗口 " + wr.W + "x" + wr.H, report);

                // 置前（不抢焦点无法滚动预热）
                Native.keybd_event(0x12, 0, 0, IntPtr.Zero);
                Native.keybd_event(0x12, 0, 2, IntPtr.Zero);
                Thread.Sleep(100);
                Native.BringWindowToTop((IntPtr)hwnd);
                Native.SetForegroundWindow((IntPtr)hwnd);
                Thread.Sleep(500);

                IntPtr orig;
                Native.POINT op; Native.GetCursorPos(out op);

                var end = DateTime.Now.AddSeconds(seconds);
                int round = 0;
                int listX = wr.L + (int)(wr.W * 0.16);
                int listY = wr.T + (int)(wr.H * 0.45);

                int panelX = wr.L + (int)(wr.W * 0.86);
                int panelY = wr.T + (int)(wr.H * 0.55);
                string panelHash0 = RegionHash((IntPtr)hwnd, wr.W - 340, 120, 300, 320);

                while (DateTime.Now < end)
                {
                    round++;
                    int before = _accum.Count;

                    // 交替滚动：左侧会话列表 + 右侧成员面板
                    // 右侧面板一旦打开，渲染成员名字会促使微信加载对应的联系人数据页
                    if (round % 2 == 1)
                    {
                        Native.SetCursorPos(listX, listY);
                        Thread.Sleep(80);
                        SendWheel(-720);
                    }
                    else
                    {
                        Native.SetCursorPos(panelX, panelY);
                        Thread.Sleep(80);
                        SendWheel(-600);
                        Thread.Sleep(120);
                        SendWheel(-600);
                    }
                    Thread.Sleep(220);
                    ScanOnce(h);
                    L("第 " + round + " 轮：累积 " + _accum.Count + " 行（+" + (_accum.Count - before) + "）", report);

                    if (round >= 4 && _accum.Count == before)
                    {
                        // 连续无新增，提前结束
                        if (round >= 6) { L("数据已稳定，提前结束采样", report); break; }
                    }
                    Thread.Sleep(150);
                }
                Native.SetCursorPos(op.X, op.Y);
                SaveCache(report);

                string panelHash1 = RegionHash((IntPtr)hwnd, wr.W - 340, 120, 300, 320);
                bool panelMoved = (panelHash0 != panelHash1);
                L(panelMoved
                    ? "检测到右侧面板区域有变化（成员面板多半是打开的，昵称覆盖会更好）"
                    : "未检测到右侧成员面板变化。提示：读取前请先在微信里点开该群的成员面板并停在屏幕上，昵称覆盖会明显提升", report);

                // 拼接
                var n2idAll = Name2Id(null);
                var n2idNew = Name2Id(_newPageAddrs);
                var pairsAll = Pairs(null, n2idAll);
                var pairsNew = Pairs(_newPageAddrs, n2idNew);
                L("映射：全量 " + n2idAll.Count + " 条 / 本次新增页 " + n2idNew.Count + " 条；成员关系：全量 " + pairsAll.Count + " / 新增页 " + pairsNew.Count, report);
                var nick = Nicknames();
                L("映射：全量 " + n2idAll.Count + " 条 / 本次新增页 " + n2idNew.Count + " 条；成员关系：全量 " + pairsAll.Count + " / 新增页 " + pairsNew.Count + "（新增页数 " + _newPageAddrs.Count + "）", report);

                // 汇总：group room id -> members
                var rooms = new Dictionary<long, HashSet<long>>();
                foreach (var pr in pairsAll)
                {
                    if (!n2idAll.ContainsKey(pr[0])) continue;
                    if (!n2idAll[pr[0]].Contains("@chatroom")) continue;
                    if (!rooms.ContainsKey(pr[0])) rooms[pr[0]] = new HashSet<long>();
                    rooms[pr[0]].Add(pr[1]);
                }

                // 判断"当前打开的群"：本次新加载页里成员数最多的那个群
                var roomsNew = new Dictionary<long, HashSet<long>>();
                foreach (var pr in pairsNew)
                {
                    if (!n2idNew.ContainsKey(pr[0])) continue;
                    if (!n2idNew[pr[0]].Contains("@chatroom")) continue;
                    if (!roomsNew.ContainsKey(pr[0])) roomsNew[pr[0]] = new HashSet<long>();
                    roomsNew[pr[0]].Add(pr[1]);
                }

                // 可用群（排除超过微信上限的脏数据）
                var usable = new Dictionary<long, HashSet<long>>();
                foreach (var kv in rooms) if (kv.Value.Count <= 500) usable[kv.Key] = kv.Value;

                RoomCounts.Clear();
                foreach (var kv in usable) RoomCounts[n2idAll[kv.Key]] = kv.Value.Count;

                // 推荐 = 可用群里、本次新增页覆盖最多的那个；否则取人数最多的可用群
                SuggestedRoom = "";
                int bestNew = 0;
                foreach (var kv in roomsNew)
                {
                    if (kv.Value.Count < 1 || kv.Value.Count > 500) continue;   // 用新增页自身的人数判断
                    if (kv.Value.Count > bestNew) { bestNew = kv.Value.Count; SuggestedRoom = n2idNew[kv.Key]; SuggestedCountFromNew = kv.Value.Count; }
                }
                if (SuggestedRoom.Length == 0 && RoomCounts.Count > 0)
                    SuggestedRoom = RoomCounts.OrderByDescending(kv => kv.Value).First().Key;

                L("解析出 " + rooms.Count + " 个群（可用 " + usable.Count + " 个）；建议读取：" + (SuggestedRoom.Length > 0 ? SuggestedRoom : "(未识别)"), report);

                var list = new List<Member>();
                foreach (var kv in usable)
                {
                    string room = n2idAll[kv.Key];
                    bool isTarget = (room == SuggestedRoom);
                    foreach (long mid in kv.Value)
                    {
                        if (!n2idNew.ContainsKey(mid) && !n2idAll.ContainsKey(mid)) continue;      // 无法映射的 id 不可信，丢弃
                        string wxid = n2idNew.ContainsKey(mid) ? n2idNew[mid] : n2idAll[mid];
                        if (!wxid.StartsWith("wxid_")) continue;   // 只保留真实用户
                        var m = new Member();
                        m.Group = room;
                        m.Wxid = wxid;
                        m.Nick = nick.ContainsKey(wxid) ? nick[wxid] : "";
                        m.InTarget = isTarget;
                        list.Add(m);
                    }
                }
                int tgt = list.Count(x => x.InTarget);
                int tgtNamed = list.Count(x => x.InTarget && x.Nick.Length > 0);

                L("昵称库 " + nick.Count + " 条；'目标群 " + tgt + " 人中 " + tgtNamed + " 人有昵称"
                    + (tgt > 0 ? "（" + (100 * tgtNamed / tgt) + "%）" : ""), report);
                L("共 " + list.Count + " 条成员记录（目标群 " + tgt + " 条）", report);
                return list;
            }
            finally { Native.CloseHandle(h); }
        }

        static string RegionHash(IntPtr hwnd, int rx, int ry, int rw, int rh)
        {
            try
            {
                Native.RECT wr;
                if (!Native.GetWindowRect(hwnd, out wr)) return "err";
                if (rw <= 0 || rh <= 0) return "err";
                using (var bmp = new Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(wr.L + rx, wr.T + ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
                    long acc = 1469598103934665603L;
                    for (int y = 0; y < rh; y += 4)
                        for (int x = 0; x < rw; x += 4)
                            acc = (acc ^ bmp.GetPixel(x, y).ToArgb()) * 1099511628211L;
                    return acc.ToString("X");
                }
            }
            catch { return "err"; }
        }

        static void SendWheel(int delta)
        {
            var inp = new Native.INPUT[1];
            inp[0].type = 0;
            inp[0].u.mi.mouseData = (uint)delta;
            inp[0].u.mi.dwFlags = Native.ME_WHEEL;
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    // 简易输入对话框
    public class Prompt : Form
    {
        TextBox box;
        Prompt(string title, string label, string initial)
        {
            Text = title;
            ClientSize = new Size(460, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            var lb = new Label();
            lb.Text = label;
            lb.AutoSize = false;
            lb.Size = new Size(420, 36);
            lb.Location = new Point(18, 14);
            Controls.Add(lb);

            box = new TextBox();
            box.Text = initial;
            box.Location = new Point(18, 54);
            box.Width = 420;
            box.Font = new Font("Microsoft YaHei UI", 10f);
            Controls.Add(box);

            var ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(266, 96);
            ok.Width = 80;
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(358, 96);
            cancel.Width = 80;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Show(string label, string title, string initial)
        {
            if (string.IsNullOrEmpty(initial))
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string cb = Clipboard.GetText().Trim();
                        if (cb.Length >= 2 && cb.Length <= 24 && cb.IndexOf('\n') < 0 && cb.IndexOf("wxid_") < 0)
                            initial = cb;
                    }
                }
                catch { }
            }
            using (var d = new Prompt(title, label, initial))
            {
                if (d.ShowDialog() != DialogResult.OK) return null;
                return d.box.Text;
            }
        }
    }

    // ============================ 半自动添加助手 ============================
    public class AddHelperForm : Form
    {
        Label lbTitle, lbWxid, lbNick, lbProgress, lbLog;
        Button btnCopy, btnNext, btnSkip, btnStop;
        List<Member> queue = new List<Member>();
        int idx = 0;
        int okCount = 0, skipCount = 0;
        string logPath;

        public AddHelperForm(List<Member> members, string group)
        {
            logPath = Path.Combine(Config.DataDir, "add-log.tsv");
            Text = "半自动添加 —— " + group;
            ClientSize = new Size(780, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;
            TopMost = true;

            var lbTip = new Label();
            lbTip.Text = "程序会把微信号复制到剪贴板并置前微信。你在微信里粘贴搜索 → 点添加到通讯录 → 回来后点「下一个」。";
            lbTip.AutoSize = false;
            lbTip.Size = new Size(730, 42);
            lbTip.Location = new Point(18, 12);
            lbTip.ForeColor = Color.FromArgb(110, 118, 128);
            Controls.Add(lbTip);

            lbTitle = new Label();
            lbTitle.Text = "待添加";
            lbTitle.AutoSize = true;
            lbTitle.Location = new Point(18, 58);
            lbTitle.ForeColor = Color.FromArgb(110, 118, 128);
            Controls.Add(lbTitle);

            lbNick = new Label();
            lbNick.AutoSize = false;
            lbNick.Size = new Size(730, 36);
            lbNick.Location = new Point(16, 80);
            lbNick.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            lbNick.ForeColor = Color.FromArgb(24, 28, 34);
            Controls.Add(lbNick);

            lbWxid = new Label();
            lbWxid.AutoSize = false;
            lbWxid.Size = new Size(730, 28);
            lbWxid.Location = new Point(18, 118);
            lbWxid.Font = new Font("Consolas", 11f);
            lbWxid.ForeColor = Color.FromArgb(7, 193, 96);
            Controls.Add(lbWxid);

            lbProgress = new Label();
            lbProgress.AutoSize = false;
            lbProgress.Size = new Size(730, 24);
            lbProgress.Location = new Point(18, 150);
            lbProgress.ForeColor = Color.FromArgb(110, 118, 128);
            Controls.Add(lbProgress);

            btnCopy = MakeBtn("复制微信号", new Point(18, 186), 120);
            btnCopy.Click += delegate { DoCopy(); };
            Controls.Add(btnCopy);

            btnNext = MakeBtn("下一个 →", new Point(146, 186), 120);
            btnNext.Click += delegate { Advance("已添加"); };
            Controls.Add(btnNext);

            btnSkip = MakeBtn("跳过", new Point(274, 186), 90);
            btnSkip.Click += delegate { Advance("跳过"); };
            Controls.Add(btnSkip);

            btnStop = MakeBtn("结束", new Point(372, 186), 90);
            btnStop.Click += delegate { Close(); };
            Controls.Add(btnStop);

            lbLog = new Label();
            lbLog.AutoSize = false;
            lbLog.Size = new Size(730, 260);
            lbLog.Location = new Point(18, 230);
            lbLog.ForeColor = Color.FromArgb(60, 66, 74);
            Controls.Add(lbLog);

            // 跳过已经处理过的
            var done = LoadDone();
            foreach (var m in members)
                if (!done.Contains(m.Wxid)) queue.Add(m);
            int skipped = members.Count - queue.Count;

            if (queue.Count == 0)
            {
                lbNick.Text = "没有待添加的人";
                lbWxid.Text = "";
                lbProgress.Text = skipped > 0 ? ("（这批 " + members.Count + " 人此前都处理过）") : "（列表为空）";
                btnCopy.Enabled = false; btnNext.Enabled = false; btnSkip.Enabled = false;
                return;
            }
            lbProgress.Text = skipped > 0 ? ("共 " + queue.Count + " 个待添加（已自动跳过 " + skipped + " 个处理过的）") : ("共 " + queue.Count + " 个待添加");
            ShowCurrent();
        }

        Button MakeBtn(string text, Point p, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Location = p;
            b.Width = w;
            b.Height = 32;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(222, 226, 232);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(24, 28, 34);
            return b;
        }

        HashSet<string> LoadDone()
        {
            var set = new HashSet<string>();
            try
            {
                if (!File.Exists(logPath)) return set;
                foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 1 && p[0].Length > 0) set.Add(p[0]);
                }
            }
            catch { }
            return set;
        }

        void ShowCurrent()
        {
            if (idx >= queue.Count)
            {
                lbNick.Text = "全部处理完毕";
                lbWxid.Text = "";
                lbProgress.Text = "成功 " + okCount + " 个，跳过 " + skipCount + " 个";
                btnCopy.Enabled = false; btnNext.Enabled = false; btnSkip.Enabled = false;
                return;
            }
            var m = queue[idx];
            lbNick.Text = string.IsNullOrEmpty(m.Nick) ? "(无昵称)" : m.Nick;
            lbWxid.Text = m.Wxid;
            lbProgress.Text = "第 " + (idx + 1) + " / " + queue.Count + " 个    成功 " + okCount + " · 跳过 " + skipCount;
            DoCopy();
        }

        void DoCopy()
        {
            if (idx >= queue.Count) return;
            try { Clipboard.SetDataObject(queue[idx].Wxid, true); }
            catch { }
            lbLog.Text = "已复制：" + queue[idx].Wxid + "\r\n请到微信里 Ctrl+V 粘贴搜索并添加。";
            try
            {
                IntPtr h = FindWeixinWindow();
                if (h != IntPtr.Zero)
                {
                    Native.ShowWindow(h, 9);
                    Native.SetForegroundWindow(h);
                }
            }
            catch { }
        }

        void Advance(string status)
        {
            if (idx >= queue.Count) return;
            var m = queue[idx];
            try
            {
                using (var sw = new StreamWriter(logPath, true, new UTF8Encoding(true)))
                    sw.WriteLine(m.Wxid + "\t" + m.Nick + "\t" + m.Group + "\t" + status + "\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            if (status == "已添加") okCount++; else skipCount++;
            idx++;
            ShowCurrent();
        }

        static IntPtr FindWeixinWindow()
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (!Native.IsWindowVisible(h)) return true;
                var cw = new StringBuilder(256);
                Native.GetClassName(h, cw, cw.Capacity);
                if (cw.ToString().Contains("QWindowIcon") && !cw.ToString().Contains("SubWindow"))
                {
                    Native.RECT r; Native.GetWindowRect(h, out r);
                    if ((long)r.W * r.H > 200000) { found = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }

    // ============================ 主窗体 ============================
    public class MainForm : Form
    {
        static readonly Color BG = Color.FromArgb(245, 246, 248);
        static readonly Color SURF = Color.White;
        static readonly Color LINE = Color.FromArgb(233, 235, 239);
        static readonly Color INK = Color.FromArgb(31, 35, 41);
        static readonly Color INK2 = Color.FromArgb(107, 114, 128);
        static readonly Color ACC = Color.FromArgb(7, 193, 96);

        TabControl tabs;
        DataGridView grid;
        ComboBox cbGroup;
        Label lbStatus, lbCount;
        TextBox tbLog;
        Button btnRead, btnExport, btnExportAll;
        NumericUpDown nudLimit, nudMin, nudMax, nudCool;

        List<Member> _all = new List<Member>();
        WxReader _reader = null;
        bool _busy = false;

        public MainForm()
        {
            Config.Load();
            Config.LoadLabels();
            BuildUi();
            RefreshGroups();
        }

        void BuildUi()
        {
            Text = "群好友助手 —— 群成员读取";
            ClientSize = new Size(1280, 860);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG;
            Font = new Font("Microsoft YaHei UI", 9f);
            MinimumSize = new Size(1000, 640);

            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 54;
            head.BackColor = SURF;
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawLine(new Pen(LINE), 0, head.Height - 1, head.Width, head.Height - 1);
                e.Graphics.FillEllipse(new SolidBrush(ACC), 20, 23, 8, 8);
            };
            var title = new Label();
            title.Text = "群好友助手";
            title.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            title.ForeColor = INK;
            title.AutoSize = true;
            title.Location = new Point(36, 17);
            head.Controls.Add(title);

            lbStatus = new Label();
            lbStatus.Text = "检测中…";
            lbStatus.ForeColor = INK2;
            lbStatus.AutoSize = true;
            lbStatus.Location = new Point(140, 20);
            head.Controls.Add(lbStatus);

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Microsoft YaHei UI", 9f);
            tabs.Padding = new Point(14, 6);

            tabs.TabPages.Add(BuildMembersPage());
            tabs.TabPages.Add(BuildLogPage());
            tabs.TabPages.Add(BuildSettingsPage());
            tabs.TabPages.Add(BuildAboutPage());

            Controls.Add(tabs);
            Controls.Add(head);

            FormClosing += delegate { Config.Save(); };

            var t = new System.Windows.Forms.Timer();
            t.Interval = 1200;
            t.Tick += delegate { UpdateStatus(); };
            t.Start();
        }

        void UpdateStatus()
        {
            if (_busy) return;
            uint pid;
            int hwnd = WxReader.FindWeixin(out pid);
            if (hwnd >= 0) { lbStatus.Text = "已连接微信（进程 " + pid + "）"; lbStatus.ForeColor = Color.FromArgb(5, 147, 74); }
            else { lbStatus.Text = "未检测到微信，请先启动并登录"; lbStatus.ForeColor = Color.FromArgb(194, 65, 12); }
        }

        TabPage BuildMembersPage()
        {
            var page = new TabPage("成员");
            page.BackColor = BG;
            page.Padding = new Padding(14);

            var bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 46;
            bar.BackColor = BG;

            btnRead = FlatButton("读取当前群成员", true);
            btnRead.Location = new Point(0, 6);
            btnRead.Click += delegate { StartRead(); };
            bar.Controls.Add(btnRead);

            var lb1 = new Label();
            lb1.Text = "群：";
            lb1.AutoSize = true;
            lb1.ForeColor = INK2;
            lb1.Location = new Point(132, 13);
            bar.Controls.Add(lb1);

            cbGroup = new ComboBox();
            cbGroup.DropDownStyle = ComboBoxStyle.DropDownList;
            cbGroup.Location = new Point(190, 8);
            cbGroup.Width = 330;
            cbGroup.SelectedIndexChanged += delegate { RefreshGrid(); };
            bar.Controls.Add(cbGroup);

            var btnAdd = FlatButton("半自动添加", true);
            btnAdd.Location = new Point(870, 6);
            btnAdd.Width = 130;
            btnAdd.Click += delegate
            {
                string sel = null;
                var gi = SelectedGroup();
                if (gi != null) sel = gi.Room;
                var targets = new List<Member>();
                foreach (DataGridViewRow r in grid.Rows)
                {
                    bool chkAdd = r.Cells[0].Value != null && (bool)r.Cells[0].Value;
                    if (!chkAdd) continue;
                    string wxidAdd = Convert.ToString(r.Cells[2].Value);
                    foreach (var m in _all) { if (m.Group == sel && m.Wxid == wxidAdd) { targets.Add(m); break; } }
                }
                if (targets.Count == 0)
                {
                    foreach (var m in _all) if (m.Group == sel && m.Wxid.StartsWith("wxid_")) targets.Add(m);
                }
                if (targets.Count == 0) { MessageBox.Show("没有可添加的成员，请先读取并选择群", "群好友助手"); return; }
                var f = new AddHelperForm(targets, sel == null ? "" : sel);
                f.Show();
                AppendLog("半自动添加：载入 " + targets.Count + " 个成员");
            };
            bar.Controls.Add(btnAdd);
            var btnOcr = FlatButton("OCR 读屏", false);
            btnOcr.Location = new Point(1150, 6);
            btnOcr.Width = 100;
            btnOcr.Click += delegate { RunOcr(); };
            bar.Controls.Add(btnOcr);

            var btnRename = FlatButton("重命名群", false);
            btnRename.Location = new Point(534, 6);
            btnRename.Width = 96;
            btnRename.Click += delegate
            {
                var gi = SelectedGroup();
                if (gi == null) { MessageBox.Show("请先读取并选择一个群", "群好友助手"); return; }
                string cur = Config.LabelOf(gi.Room);
                string input = Prompt.Show("给这个群起个名字（只存在本机，方便你自己辨认）", "重命名群", cur == gi.Room ? "" : cur);
                if (input == null) return;
                input = input.Trim();
                if (input.Length == 0) Config.Labels.Remove(gi.Room);
                else Config.Labels[gi.Room] = input;
                Config.SaveLabels();
                int idx = cbGroup.SelectedIndex;
                RefreshGroups();
                if (idx >= 0 && idx < cbGroup.Items.Count) cbGroup.SelectedIndex = idx;
                RefreshGrid();
            };
            bar.Controls.Add(btnRename);

            btnExport = FlatButton("导出勾选", true);
            btnExport.Location = new Point(640, 6);
            btnExport.Click += delegate { Export(false); };
            bar.Controls.Add(btnExport);

            btnExportAll = FlatButton("导出本群全部", false);
            btnExportAll.Location = new Point(750, 6);
            btnExportAll.Click += delegate { Export(true); };
            bar.Controls.Add(btnExportAll);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = SURF;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(242, 243, 246);
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(252, 252, 253);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = INK2;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(252, 252, 253);
            grid.ColumnHeadersHeight = 32;
            grid.RowTemplate.Height = 34;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 248, 239);
            grid.DefaultCellStyle.SelectionForeColor = INK;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);

            var chk = new DataGridViewCheckBoxColumn();
            chk.Name = "chk";
            chk.HeaderText = "";
            chk.Width = 40;
            chk.FillWeight = 8;
            grid.Columns.Add(chk);
            grid.Columns.Add(Col("nick", "昵称", 22));
            grid.Columns.Add(Col("wxid", "微信号", 40));
            grid.Columns.Add(Col("group", "来源群", 30));

            var foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = 44;
            foot.BackColor = Color.FromArgb(252, 252, 253);
            foot.Paint += delegate(object s, PaintEventArgs e) { e.Graphics.DrawLine(new Pen(LINE), 0, 0, foot.Width, 0); };

            lbCount = new Label();
            lbCount.Text = "尚未读取";
            lbCount.AutoSize = true;
            lbCount.ForeColor = INK2;
            lbCount.Location = new Point(4, 14);
            foot.Controls.Add(lbCount);

            var btnAll = FlatButton("全选", false);
            btnAll.Location = new Point(760, 8);
            btnAll.Click += delegate { SetAllChecked(true); };
            foot.Controls.Add(btnAll);

            var btnNone = FlatButton("清空", false);
            btnNone.Location = new Point(846, 8);
            btnNone.Click += delegate { SetAllChecked(false); };
            foot.Controls.Add(btnNone);

            page.Controls.Add(grid);
            page.Controls.Add(foot);
            page.Controls.Add(bar);
            return page;
        }

        static DataGridViewTextBoxColumn Col(string name, string head, int weight)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = head; c.FillWeight = weight; c.ReadOnly = true;
            return c;
        }

        TabPage BuildLogPage()
        {
            var page = new TabPage("运行日志");
            page.BackColor = BG;
            page.Padding = new Padding(14);
            tbLog = new TextBox();
            tbLog.Multiline = true;
            tbLog.ScrollBars = ScrollBars.Vertical;
            tbLog.Dock = DockStyle.Fill;
            tbLog.BackColor = SURF;
            tbLog.ForeColor = INK2;
            tbLog.BorderStyle = BorderStyle.FixedSingle;
            tbLog.Font = new Font("Consolas", 9f);
            tbLog.ReadOnly = true;
            page.Controls.Add(tbLog);
            return page;
        }

        TabPage BuildSettingsPage()
        {
            var page = new TabPage("设置");
            page.BackColor = BG;

            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.AutoScroll = true;
            p.Padding = new Padding(16);

            int y = 10;
            nudLimit = AddNum(p, ref y, "每日添加上限", "超过后当天不再发送（建议 ≤ 50）", 1, 200, Config.DailyLimit);
            nudMin = AddNum(p, ref y, "批内最小间隔", "两个人之间的随机间隔下限（秒）", 10, 600, Config.IntervalMin);
            nudMax = AddNum(p, ref y, "批内最大间隔", "两个人之间的随机间隔上限（秒）", 10, 900, Config.IntervalMax);
            nudCool = AddNum(p, ref y, "风控冷却（小时）", "出现操作频繁提示后暂停的时长", 1, 72, Config.CooldownHours);

            var warn = new Label();
            warn.Text = "提示：低于 1 小时的冷却通常无法解除微信限制，连续重试会显著提高封号风险。\r\n本程序当前版本只负责读取名单，不会自动发送好友申请。";
            warn.ForeColor = Color.FromArgb(180, 83, 9);
            warn.Location = new Point(18, y + 16);
            warn.Size = new Size(700, 44);
            p.Controls.Add(warn);

            var save = FlatButton("保存设置", true);
            save.Location = new Point(18, y + 70);
            save.Click += delegate
            {
                Config.DailyLimit = (int)nudLimit.Value;
                Config.IntervalMin = (int)nudMin.Value;
                Config.IntervalMax = (int)nudMax.Value;
                Config.CooldownHours = (int)nudCool.Value;
                Config.Save();
                MessageBox.Show("设置已保存", "群好友助手");
            };
            p.Controls.Add(save);

            page.Controls.Add(p);
            return page;
        }

        NumericUpDown AddNum(Panel p, ref int y, string title, string desc, int min, int max, int val)
        {
            var l1 = new Label();
            l1.Text = title; l1.AutoSize = true; l1.ForeColor = INK;
            l1.Font = new Font("Microsoft YaHei UI", 9.5f);
            l1.Location = new Point(18, y);
            p.Controls.Add(l1);

            var l2 = new Label();
            l2.Text = desc; l2.AutoSize = true; l2.ForeColor = INK2;
            l2.Location = new Point(18, y + 22);
            p.Controls.Add(l2);

            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = Math.Min(max, Math.Max(min, val));
            n.Location = new Point(620, y + 4);
            n.Width = 90;
            p.Controls.Add(n);

            y += 62;
            return n;
        }

        TabPage BuildAboutPage()
        {
            var page = new TabPage("关于");
            page.BackColor = BG;
            var t = new TextBox();
            t.Multiline = true;
            t.Dock = DockStyle.Fill;
            t.ReadOnly = true;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = BG;
            t.ForeColor = INK2;
            t.ScrollBars = ScrollBars.Vertical;
            t.Font = new Font("Microsoft YaHei UI", 9f);
            t.Text =
                "群好友助手  1.1.0\r\n\r\n" +
                "功能：读取当前登录微信的群成员名单，支持导出 CSV。\r\n\r\n" +
                "工作原理：微信 4.x 的数据库使用 SQLCipher 加密，但其解密后的数据页会常驻在微信进程内存中。\r\n" +
                "本程序只读取这些已解密的内存页并还原成员关系，不修改任何数据、不发送任何网络请求。\r\n\r\n" +
                "为什么需要管理员权限：微信本身以管理员权限运行，权限较低的进程无法读取它的内存。\r\n\r\n" +
                "限制说明：\r\n" +
                "· 只能读到微信「已经加载过」的数据。会话列表里出现过的联系人可读到，群里从未出现过的沉默成员读不到。\r\n" +
                "· 要读取某个群的完整名单，请先在微信中打开该群的成员面板，再点「读取群成员」。\r\n" +
                "· 昵称为尽力推断，可能为空或不准确；微信号（wxid）是可靠字段。\r\n\r\n" +
                "!! 数据准确性说明（重要）\r\n" +
                "微信每个数据库都有独立的 id 编号空间（contact.db 的 Name2Id、message.db 的 ChatName2Id 等），\r\n" +
                "而这些内存页在进程里是交错存放的，程序无法判断某一页属于哪个库。\r\n" +
                "结果是：同一个 id 号可能被不同库复用，导致不同群的成员被合并 —— 人数会偏多、可能混入其它群的人。\r\n" +
                "实测 2287 个 id 中有 1605 个（70%）存在这种冲突。\r\n\r\n" +
                "因此：群号和微信号字段本身是真实的，但「某人在某群」这个关系无法保证精确。\r\n" +
                "如果要精确名单，只有两条路：① 拿到数据库密钥后直接解密 contact.db；② 用能看屏幕的模型做 OCR。\r\n\r\n" +
                "合规提示：本工具仅供导出自己设备上的数据用于个人整理。批量添加好友违反微信用户协议，\r\n" +
                "存在账号被封风险，本版本不提供该功能。";
            page.Controls.Add(t);
            return page;
        }

        Button FlatButton(string text, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? ACC : Color.FromArgb(223, 226, 232);
            b.BackColor = primary ? ACC : SURF;
            b.ForeColor = primary ? Color.White : INK;
            b.Font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            b.Height = 32;
            b.Width = 120;
            b.Cursor = Cursors.Hand;
            return b;
        }

        // ---------------- 读取 ----------------
        void StartRead()
        {
            if (_busy) return;
            uint pid;
            if (WxReader.FindWeixin(out pid) < 0)
            {
                MessageBox.Show("未检测到微信窗口。\r\n请先启动微信并登录，然后重试。", "群好友助手");
                return;
            }

            _busy = true;
            btnRead.Enabled = false;
            btnRead.Text = "读取中…";
            tbLog.Text = "";

            int seconds = 60;
            var th = new Thread(delegate()
            {
                List<Member> result = null;
                string err = null;
                try
                {
                    var r = new WxReader();
                    _reader = r;
                    result = r.Read(seconds, delegate(string s) { AppendLog(s); });
                }
                catch (Exception ex) { err = ex.Message; }

                BeginInvoke(new Action(delegate
                {
                    _busy = false;
                    btnRead.Enabled = true;
                    btnRead.Text = "读取当前群成员";
                    if (err != null)
                    {
                        MessageBox.Show("读取失败：" + err, "群好友助手");
                        return;
                    }
                    if (result == null) return;
                    _all = result;
                    SaveSnapshot();
                    RefreshGroups();
                    AppendLog("完成，共 " + _all.Count + " 条记录");

                    // 昵称覆盖偏低时主动提示（昵称来自联系人表，只有被微信加载过的人才有）
                    int total = 0, named = 0;
                    foreach (var m in _all) { total++; if (m.Nick.Length > 0) named++; }
                    if (total > 10 && named * 2 < total)
                    {
                        MessageBox.Show(
                            "成员已读出（" + total + " 条），但只有 " + named + " 条带昵称。\r\n\r\n"
                            + "原因：昵称存在微信的「联系人表」里，微信只在用到某个人时才把它解密进内存。\r\n"
                            + "群里没聊过天的成员，微信从来没加载过他们的资料，所以读不到昵称。\r\n\r\n"
                            + "提高办法：在微信里打开该群 → 点开「群成员」面板 → 让它停在屏幕上并往下滚几屏 → 再点一次读取。\r\n"
                            + "（微信号 wxid 是可靠的，导出时以它为准）",
                            "关于昵称", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    if (_all.Count == 0)
                        MessageBox.Show("没有读到成员数据。\r\n\r\n请先在微信里打开目标群的聊天窗口，并点开群成员面板、滚动几屏，然后再点读取。", "群好友助手");
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void AppendLog(string s)
        {
            if (tbLog.IsDisposed) return;
            if (tbLog.InvokeRequired)
            {
                tbLog.BeginInvoke(new Action(delegate { AppendLog(s); }));
                return;
            }
            tbLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }

        void SaveSnapshot()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("群号\t微信号\t昵称");
                foreach (var m in _all) sb.AppendLine(m.Group + "\t" + m.Wxid + "\t" + m.Nick);
                File.WriteAllText(Path.Combine(Config.DataDir, "last-read.tsv"), sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        void RefreshGroups()
        {
            // 只列群，由用户手动选择；按人数从多到少排列，默认选第一个
            var counts = new Dictionary<string, int>();
            foreach (var m in _all)
            {
                if (!counts.ContainsKey(m.Group)) counts[m.Group] = 0;
                counts[m.Group]++;
            }

            cbGroup.Items.Clear();
            foreach (var kv in counts.OrderByDescending(kv => kv.Value))
            {
                var gi = new GroupItem();
                gi.Room = kv.Key;
                gi.Count = kv.Value;
                gi.IsJustLoaded = (_reader != null && _reader.SuggestedRoom == kv.Key);
                cbGroup.Items.Add(gi);
            }
            if (cbGroup.Items.Count > 0) cbGroup.SelectedIndex = 0;

            RefreshGrid();
        }

        GroupItem SelectedGroup()
        {
            return cbGroup.SelectedItem as GroupItem;
        }

        void RefreshGrid()
        {
            if (grid == null) return;
            var gi = SelectedGroup();
            grid.Rows.Clear();
            int n = 0;
            if (gi != null)
            {
                foreach (var m in _all)
                {
                    if (m.Group != gi.Room) continue;
                    int i = grid.Rows.Add();
                    grid.Rows[i].Cells[0].Value = m.Checked;
                    grid.Rows[i].Cells[1].Value = m.Nick;
                    grid.Rows[i].Cells[2].Value = m.Wxid;
                    grid.Rows[i].Cells[3].Value = m.Group;
                    n++;
                }
            }
            if (gi == null) { lbCount.Text = "尚未读取，请点右上角「读取当前群成员」"; return; }
            int named = 0;
            foreach (var m in _all) if (m.Group == gi.Room && m.Nick.Length > 0) named++;
            string tip = (n > 0 && named * 2 < n) ? "  · 昵称覆盖偏低，请在微信里打开该群成员面板滚动后再读一次" : "";
            lbCount.Text = "已选群：" + gi.Room + " · " + n + " 人（估算） · 昵称 " + named + " 条" + tip + "（共读到 " + _all.Count + " 条 / "
                + _all.Select(x => x.Group).Distinct().Count() + " 个群）";
        }

        void SetAllChecked(bool v)
        {
            foreach (DataGridViewRow r in grid.Rows) r.Cells[0].Value = v;
        }

        // 用 Windows OCR 读屏：拿到群名、人数、成员昵称（只截图，不点击不输入）
        void RunOcr()
        {
            if (_busy) { MessageBox.Show("正在读取中，请稍候", "群好友助手"); return; }
            string exe = Path.Combine(Application.StartupPath, "wxocr.exe");
            string csv = Path.Combine(Config.DataDir, "ocr-result.csv");
            if (!File.Exists(exe)) { MessageBox.Show("缺少 wxocr.exe", "群好友助手"); return; }

            try
            {
                AppendLog("开始 OCR 读屏…（请确保微信在前面且已打开目标群/成员面板）");
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = "3 \"" + csv + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                var pr = Process.Start(psi);
                pr.WaitForExit(120000);
                AppendLog("OCR 结束，退出码 " + pr.ExitCode);

                if (!File.Exists(csv)) { MessageBox.Show("OCR 没有产出结果", "群好友助手"); return; }
                string gName = "", gCount = "";
                var names = new List<string>();
                foreach (var line in File.ReadAllLines(csv, Encoding.UTF8))
                {
                    var f = line.Split(',');
                    if (f.Length < 2) continue;
                    if (f[0] == "群名") gName = f[1];
                    else if (f[0] == "人数") gCount = f[1];
                    else if (f[0] == "成员" && f[1].Length > 0) names.Add(f[1]);
                }
                AppendLog("OCR 结果：群名=" + gName + " 人数=" + gCount + " 成员名=" + names.Count);

                if (names.Count == 0)
                {
                    MessageBox.Show(
                        "屏幕上没有识别到成员名单。\r\n\r\n请在微信里：\r\n" +
                        "1) 打开目标群\r\n2) 点开「群成员」面板（看到一排排头像）\r\n3) 保持微信在前台，再点一次本按钮\r\n\r\n" +
                        (gName.Length > 0 ? "（已识别到群名：" + gName + "）" : ""),
                        "OCR 读屏");
                    return;
                }

                // 把 OCR 结果放进列表
                string room = gName.Length > 0 ? gName : "(OCR 未识别群名)";
                _all = new List<Member>();
                foreach (var n in names)
                {
                    var m = new Member();
                    m.Group = room;
                    m.Nick = n;
                    m.Wxid = "";
                    m.InTarget = true;
                    _all.Add(m);
                }
                _reader = null;
                RefreshGroups();
                if (gName.Length > 0 && gCount.Length > 0)
                    Config.Labels[room] = gName;
                lbCount.Text = "OCR 结果：" + room + (gCount.Length > 0 ? "（微信显示 " + gCount + " 人）" : "")
                    + " · 识别到 " + names.Count + " 个名字";
            }
            catch (Exception ex)
            {
                AppendLog("OCR 失败: " + ex.Message);
                MessageBox.Show("OCR 失败：" + ex.Message, "群好友助手");
            }
        }

        void Export(bool all)
        {
            if (_all.Count == 0) { MessageBox.Show("还没有数据，请先点「读取当前群成员」", "群好友助手"); return; }
            var gi = SelectedGroup();
            if (gi == null) { MessageBox.Show("请先在左侧下拉框中选择一个群", "群好友助手"); return; }
            string sel = gi.Room;

            var rows = new List<Member>();
            if (all)
            {
                foreach (var m in _all) if (m.Group == sel) rows.Add(m);
            }
            else
            {
                foreach (DataGridViewRow r in grid.Rows)
                {
                    bool chk = r.Cells[0].Value != null && (bool)r.Cells[0].Value;
                    if (!chk) continue;
                    string wxid = Convert.ToString(r.Cells[2].Value);
                    foreach (var m in _all)
                    {
                        if (m.Group != sel || m.Wxid != wxid) continue;
                        m.Checked = true;
                        rows.Add(m);
                        break;
                    }
                }
                if (rows.Count == 0)
                {
                    MessageBox.Show("没有勾选任何成员。", "群好友助手");
                    return;
                }
            }

            var dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件 (*.csv)|*.csv";
            dlg.FileName = sel.Replace("@chatroom", "") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("群号,微信号,昵称");
                foreach (var m in rows) sb.AppendLine(Csv(m.Group) + "," + Csv(m.Wxid) + "," + Csv(m.Nick));
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("已导出 " + rows.Count + " 条到：\r\n" + dlg.FileName, "群好友助手");
            }
            catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message, "群好友助手"); }
        }

        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.Contains(",") || s.Contains("\"")) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    static class Program
    {
        static void CliRead(int seconds)
        {
            string logPath = Path.Combine(Config.DataDir, "cli-read.log");
            var sb = new StringBuilder();
            Action<string> log = delegate(string s) { sb.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s); };
            try
            {
                uint pid;
                int hwnd = WxReader.FindWeixin(out pid);
                log("cli read start, weixin hwnd=" + hwnd + " pid=" + pid);
                var r = new WxReader();
                var list = r.Read(seconds, log);
                if (list == null) { log("RESULT=FAIL read returned null"); }
                else
                {
                    log("RESULT=OK all_members=" + list.Count + " groups=" + list.Select(m => m.Group).Distinct().Count()
                        + " target=" + (r.SuggestedRoom.Length > 0 ? r.SuggestedRoom : "(none)"));
                    log("room_counts=" + string.Join(" | ", r.RoomCounts.OrderByDescending(kv => kv.Value).Take(8)
                        .Select(kv => kv.Key + ":" + kv.Value).ToArray()));
                    var focused = new List<Member>();
                    foreach (var m in list) if (r.SuggestedRoom.Length > 0 && m.Group == r.SuggestedRoom) focused.Add(m);
                    var useRows = focused.Count > 0 ? focused : list;
                    var outCsv = new StringBuilder();
                    outCsv.AppendLine("群号,微信号,昵称");
                    foreach (var m in useRows) outCsv.AppendLine(m.Group + "," + m.Wxid + "," + m.Nick);
                    File.WriteAllText(Path.Combine(Config.DataDir, "cli-read.csv"), outCsv.ToString(), new UTF8Encoding(true));
                    log("written cli-read.csv rows=" + useRows.Count);

                    // 额外导出：全部去重微信号（供"取微信号"工具使用）
                    var allWx = new List<string>();
                    var seenWx = new HashSet<string>();
                    foreach (var m in list)
                    {
                        if (!m.Wxid.StartsWith("wxid_")) continue;
                        if (seenWx.Add(m.Wxid)) allWx.Add(m.Wxid);
                    }
                    allWx.Sort();
                    var wsb = new StringBuilder();
                    wsb.AppendLine("微信号");
                    foreach (var w in allWx) wsb.AppendLine(w);
                    File.WriteAllText(Path.Combine(Config.DataDir, "wxid-all.txt"), wsb.ToString(), new UTF8Encoding(true));
                    log("written wxid-all.txt count=" + allWx.Count);
                }
            }
            catch (Exception ex) { log("RESULT=EXCEPTION " + ex.GetType().Name + " " + ex.Message); }
            try { File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }

        [STAThread]
        static void Main()
        {
            try
            {
                Native.SetProcessDPIAware();
                var cli = Environment.GetCommandLineArgs();
                for (int i = 0; i < cli.Length; i++)
                {
                    if (cli[i] == "--addtest")
                    {
                        var list = new List<Member>();
                        for (int k = 0; k < 3; k++)
                        {
                            var mm = new Member();
                            mm.Wxid = "wxid_test" + k + "abcdef22";
                            mm.Nick = k == 0 ? "测试昵称一" : "";
                            mm.Group = "test@chatroom";
                            list.Add(mm);
                        }
                        var f = new AddHelperForm(list, "自检群");
                        var tmr = new System.Windows.Forms.Timer();
                        tmr.Interval = 2500;
                        tmr.Tick += delegate { tmr.Stop(); f.Close(); };
                        tmr.Start();
                        Application.Run(f);
                        return;
                    }
                    if (cli[i] == "--read")
                    {
                        int sec = 45;
                        if (i + 1 < cli.Length) int.TryParse(cli[i + 1], out sec);
                        CliRead(sec);
                        return;
                    }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 权限自检
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                if (!pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    MessageBox.Show(
                        "本程序需要管理员权限才能读取微信内存。\r\n\r\n请右键点击程序 → 「以管理员身份运行」，或使用安装包自带的启动方式。",
                        "群好友助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "群好友助手");
            }
        }
    }
}
