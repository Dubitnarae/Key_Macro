// 매크로 데이터·윈도우 입력 API·키보드/마우스 훅·실행기 (화면과 무관한 부분)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KeyMacro
{
    // ───────────────────────── 윈도우 API ─────────────────────────
    static class W
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public U u; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }   // KBDLLHOOKSTRUCT
        [StructLayout(LayoutKind.Sequential)]
        public struct MSL { public int x, y; public uint data, flags, time; public IntPtr extra; }   // MSLLHOOKSTRUCT
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        public delegate IntPtr HookProc(int n, IntPtr wp, IntPtr lp);

        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr hwnd, uint min, uint max);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    }

    static class Input
    {
        static readonly HashSet<int> Ext = new HashSet<int> { 33, 34, 35, 36, 37, 38, 39, 40, 44, 45, 46, 91, 92, 93, 111, 144, 163, 165 };   // 확장 키
        public static readonly HashSet<int> Mods = new HashSet<int> { 16, 17, 18, 160, 161, 162, 163, 164, 165 };
        public static readonly Dictionary<string, uint[]> BtnFlag = new Dictionary<string, uint[]>
        {
            { "left", new uint[] { 2, 4 } }, { "right", new uint[] { 8, 0x10 } }, { "middle", new uint[] { 0x20, 0x40 } },   // 누르기, 떼기
        };

        static uint Send(W.INPUT inp)
        {
            return W.SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(W.INPUT)));
        }

        static uint Kbd(ushort vk, ushort scan, uint flags)
        {
            var i = new W.INPUT { type = 1 };
            i.u.ki = new W.KEYBDINPUT { vk = vk, scan = scan, flags = flags };
            return Send(i);
        }

        public static uint Key(int vk, bool up)
        {
            return Kbd((ushort)vk, (ushort)W.MapVirtualKeyW((uint)vk, 0), (up ? 2u : 0u) | (Ext.Contains(vk) ? 1u : 0u));
        }

        public static uint Mouse(uint flags, int data)
        {
            var i = new W.INPUT { type = 0 };
            i.u.mi = new W.MOUSEINPUT { data = unchecked((uint)data), flags = flags };
            return Send(i);
        }

        // 유니코드로 직접 입력 → 한/영 상태와 무관. wait 가 true 를 돌려주면(중지) 멈춘다.
        public static void Text(string s, Func<double, bool> wait)
        {
            foreach (char ch in s)
            {
                if (ch == '\n') { Key(13, false); Key(13, true); }
                else if (ch != '\r') { Kbd(0, ch, 4); Kbd(0, ch, 6); }
                if (wait(0.005)) return;
            }
        }

        public static int[] Cursor()
        {
            W.POINT p;
            W.GetCursorPos(out p);
            return new[] { p.x, p.y };
        }

        // 화면 색 (r, g, b), 읽지 못하면 null
        public static int[] Pixel(int x, int y)
        {
            IntPtr dc = W.GetDC(IntPtr.Zero);
            uint c = W.GetPixel(dc, x, y);
            W.ReleaseDC(IntPtr.Zero, dc);
            return c == 0xFFFFFFFF ? null : new[] { (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255) };
        }

        // 지금 눌린 조합키: Ctrl=1, Alt=2, Shift=4
        public static int ModsNow()
        {
            Func<int, int> down = vk => (W.GetAsyncKeyState(vk) & 0x8000) != 0 ? 1 : 0;
            return down(17) | down(18) << 1 | down(16) << 2;
        }
    }

    // 키보드·마우스 감시. 따로 스레드에서 메시지를 돌려 화면이 바빠도 입력이 늦어지지 않게 한다.
    static class Hook
    {
        public static volatile bool RecOn;   // 기록 중일 때만 마우스 이벤트를 넘긴다
        public static Action<int, bool, double> Key;           // (가상키, 눌림, 시각) — 훅 스레드에서 불린다
        public static Action<int, int, int, int, double> Mouse;  // (메시지, x, y, 휠, 시각)
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static W.HookProc kb, ms;   // 대리자가 GC 로 사라지지 않게 붙잡아 둔다

        public static double Now() { return Clock.Elapsed.TotalSeconds; }

        public static bool Start()
        {
            bool ok = false;
            var ready = new ManualResetEvent(false);
            var th = new Thread(() =>
            {
                kb = (n, wp, lp) =>
                {
                    if (n == 0)
                    {
                        var k = (W.KBD)Marshal.PtrToStructure(lp, typeof(W.KBD));
                        var f = Key;
                        if ((k.flags & 0x10) == 0 && f != null)   // 매크로가 보낸 입력은 무시
                            try { f((int)k.vk, (int)wp == 0x100 || (int)wp == 0x104, Now()); } catch { }
                    }
                    return W.CallNextHookEx(IntPtr.Zero, n, wp, lp);
                };
                ms = (n, wp, lp) =>
                {
                    if (n == 0 && RecOn)
                    {
                        var m = (W.MSL)Marshal.PtrToStructure(lp, typeof(W.MSL));
                        var f = Mouse;
                        if ((m.flags & 1) == 0 && f != null)
                            try { f((int)wp, m.x, m.y, (short)(m.data >> 16), Now()); } catch { }
                    }
                    return W.CallNextHookEx(IntPtr.Zero, n, wp, lp);
                };
                IntPtr mod = W.GetModuleHandleW(null);
                ok = W.SetWindowsHookExW(13, kb, mod, 0) != IntPtr.Zero && W.SetWindowsHookExW(14, ms, mod, 0) != IntPtr.Zero;
                ready.Set();
                W.MSG msg;
                while (W.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0) { }
            });
            th.IsBackground = true;
            th.Start();
            ready.WaitOne(5000);
            return ok;
        }
    }

    // ───────────────────────── 데이터 ─────────────────────────
    // 이벤트 한 줄. 저장 형식(매크로.json)은 파이썬판과 같다.
    class Ev
    {
        public string T, Act = "click", Move = "abs", Btn = "left", S = "", Name = "";
        public int Vk, X, Y, Wheel, Rand, R, G, B, Tol = 10;
        public double Sec = 1.0;

        public static Ev New(string t) { return new Ev { T = t }; }

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object> { { "t", T } };
            switch (T)
            {
                case "key": d.Add("vk", Vk); d.Add("act", Act); break;
                case "mouse":
                    d.Add("move", Move); d.Add("x", X); d.Add("y", Y); d.Add("btn", Btn); d.Add("act", Act); d.Add("wheel", Wheel);
                    break;
                case "delay": d.Add("sec", Sec); d.Add("rand", Rand); break;
                case "color": d.Add("x", X); d.Add("y", Y); d.Add("r", R); d.Add("g", G); d.Add("b", B); d.Add("tol", Tol); break;
                case "text": d.Add("s", S); break;
                case "call": d.Add("name", Name); break;
            }
            return d;
        }

        public static Ev FromDict(IDictionary<string, object> d)
        {
            var e = new Ev { T = (string)d["t"] };
            if (!Names.Fields.ContainsKey(e.T)) throw new FormatException("모르는 이벤트 종류: " + e.T);
            foreach (var kv in d)
            {
                object v = kv.Value;
                switch (kv.Key)
                {
                    case "vk": e.Vk = Num.I(v); break;
                    case "act": e.Act = (string)v; break;
                    case "move": e.Move = (string)v; break;
                    case "x": e.X = Num.I(v); break;
                    case "y": e.Y = Num.I(v); break;
                    case "btn": e.Btn = (string)v; break;
                    case "wheel": e.Wheel = Num.I(v); break;
                    case "sec": e.Sec = Num.D(v); break;
                    case "rand": e.Rand = Num.I(v); break;
                    case "r": e.R = Num.I(v); break;
                    case "g": e.G = Num.I(v); break;
                    case "b": e.B = Num.I(v); break;
                    case "tol": e.Tol = Num.I(v); break;
                    case "s": e.S = (string)v; break;
                    case "name": e.Name = (string)v; break;
                }
            }
            return e;
        }

        public Ev Clone() { return FromDict(ToDict()); }
        public string Json() { return Store.Js.Serialize(ToDict()); }
    }

    class Macro
    {
        public string Name = "";
        public int Repeat = 1;
        public int[] Start, Stop;   // [가상키, 조합키] 또는 null
        public List<Ev> Events = new List<Ev>();

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "name", Name }, { "repeat", Repeat }, { "start", Start }, { "stop", Stop },
                { "events", Events.Select(e => e.ToDict()).ToArray() },
            };
        }

        public static Macro FromDict(IDictionary<string, object> d)
        {
            return new Macro
            {
                Name = (string)d["name"], Repeat = Num.I(d["repeat"]), Start = Num.Hk(d["start"]), Stop = Num.Hk(d["stop"]),
                Events = ((System.Collections.IEnumerable)d["events"]).Cast<IDictionary<string, object>>().Select(Ev.FromDict).ToList(),
            };
        }

        public Macro Clone() { return FromDict(ToDict()); }
    }

    static class Num
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static int I(object v) { return Convert.ToInt32(v, Inv); }
        public static double D(object v) { return Convert.ToDouble(v, Inv); }

        public static int[] Hk(object v)
        {
            if (v == null) return null;
            var a = ((System.Collections.IEnumerable)v).Cast<object>().Select(I).ToArray();
            if (a.Length != 2) throw new FormatException("단축키 형식");
            return a;
        }

        public static bool HkEq(int[] a, int[] b) { return a != null && b != null && a[0] == b[0] && a[1] == b[1]; }
    }

    class Data
    {
        public Dictionary<string, int[]> Settings = Store.DefaultSettings();
        public List<Macro> Macros = new List<Macro>();
    }

    static class Store
    {
        public static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public static Dictionary<string, int[]> DefaultSettings()   // F8, F9, F7
        {
            return new Dictionary<string, int[]> { { "toggle", new[] { 119, 0 } }, { "record", new[] { 120, 0 } }, { "capture", new[] { 118, 0 } } };
        }

        // 읽지 못하면 파일을 따로 옮겨 두고(덮어써서 잃지 않도록) 빈 목록으로 시작한다. warn 에 안내문.
        public static Data Load(string path, out string warn)
        {
            warn = null;
            var data = new Data();
            if (!File.Exists(path)) return data;
            try
            {
                var d = (Dictionary<string, object>)Js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                foreach (var kv in (Dictionary<string, object>)d["settings"])
                    if (data.Settings.ContainsKey(kv.Key)) data.Settings[kv.Key] = Num.Hk(kv.Value) ?? data.Settings[kv.Key];
                data.Macros = ((object[])d["macros"]).Cast<IDictionary<string, object>>().Select(Macro.FromDict).ToList();
            }
            catch (Exception e)
            {
                if (e is IOException || e is UnauthorizedAccessException) throw;
                string bad = Path.ChangeExtension(path, ".깨짐_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                File.Move(path, bad);
                warn = "매크로 파일을 읽지 못했습니다.\n" + Path.GetFileName(bad) + " 로 옮겨 두고 빈 목록으로 시작합니다.";
                data = new Data();
            }
            return data;
        }

        public static void Save(string path, Data data)
        {
            var d = new Dictionary<string, object>
            {
                { "settings", data.Settings }, { "macros", data.Macros.Select(m => m.ToDict()).ToArray() },
            };
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Js.Serialize(d), new UTF8Encoding(false));   // BOM 없이 (파이썬판도 읽을 수 있게)
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // ───────────────────────── 이름·설명 ─────────────────────────
    static class Names
    {
        static readonly Dictionary<int, string> VK = MakeVk();

        static Dictionary<int, string> MakeVk()
        {
            var v = new Dictionary<int, string>
            {
                { 8, "Backspace" }, { 9, "Tab" }, { 13, "Enter" }, { 19, "Pause" }, { 20, "CapsLock" }, { 21, "한/영" }, { 25, "한자" },
                { 27, "Esc" }, { 32, "Space" }, { 33, "PageUp" }, { 34, "PageDown" }, { 35, "End" }, { 36, "Home" }, { 37, "←" },
                { 38, "↑" }, { 39, "→" }, { 40, "↓" }, { 44, "PrintScreen" }, { 45, "Insert" }, { 46, "Delete" }, { 91, "Win" },
                { 92, "RWin" }, { 93, "Menu" }, { 106, "Num*" }, { 107, "Num+" }, { 109, "Num-" }, { 110, "Num." }, { 111, "Num/" },
                { 144, "NumLock" }, { 145, "ScrollLock" }, { 160, "Shift" }, { 161, "RShift" }, { 162, "Ctrl" }, { 163, "RCtrl" },
                { 164, "Alt" }, { 165, "RAlt" }, { 186, ";" }, { 187, "=" }, { 188, "," }, { 189, "-" }, { 190, "." }, { 191, "/" },
                { 192, "`" }, { 219, "[" }, { 220, "\\" }, { 221, "]" }, { 222, "'" },
            };
            for (int c = 48; c < 58; c++) v[c] = ((char)c).ToString();
            for (int c = 65; c < 91; c++) v[c] = ((char)c).ToString();
            for (int i = 0; i < 10; i++) v[96 + i] = "Num" + i;
            for (int i = 1; i <= 24; i++) v[111 + i] = "F" + i;
            return v;
        }

        public static readonly Dictionary<string, string> Act = new Dictionary<string, string> { { "click", "누르고 떼기" }, { "down", "누르기" }, { "up", "떼기" } };
        public static readonly Dictionary<string, string> Move = new Dictionary<string, string> { { "abs", "절대 위치로 이동" }, { "rel", "현재 위치에서 이동" }, { "none", "이동 안 함" } };
        public static readonly Dictionary<string, string> Btn = new Dictionary<string, string> { { "left", "왼쪽" }, { "right", "오른쪽" }, { "middle", "가운데" }, { "none", "없음" } };

        // 이벤트 종류 → (이름, 입력 칸). 칸 종류: int, float, choice, key, hotkey, text
        public static readonly Dictionary<string, Tuple<string, Field[]>> Fields = new Dictionary<string, Tuple<string, Field[]>>
        {
            { "key", Tuple.Create("키보드", new[] { new Field("vk", "키 (버튼을 누르고 키 입력)", "key"), new Field("act", "동작", Act) }) },
            { "mouse", Tuple.Create("마우스", new[] { new Field("move", "이동", Move), new Field("x", "X", "int"), new Field("y", "Y", "int"),
                new Field("btn", "버튼", Btn), new Field("act", "동작", Act), new Field("wheel", "휠 (위 120, 아래 -120, 안 쓰면 0)", "int") }) },
            { "delay", Tuple.Create("지연", new[] { new Field("sec", "시간(초)", "float"), new Field("rand", "랜덤 ±%", "int") }) },
            { "color", Tuple.Create("색 대기", new[] { new Field("x", "X", "int"), new Field("y", "Y", "int"), new Field("r", "R", "int"),
                new Field("g", "G", "int"), new Field("b", "B", "int"), new Field("tol", "R·G·B 허용 편차", "int") }) },
            { "text", Tuple.Create("문자열", new[] { new Field("s", "입력할 글", "text") }) },
            { "call", Tuple.Create("매크로 호출", new Field[0]) },
        };

        public static readonly Dictionary<string, Func<Ev, string>> Check = new Dictionary<string, Func<Ev, string>>
        {
            { "mouse", r => r.Move == "none" && r.Btn == "none" && r.Wheel == 0 ? "이동·버튼·휠 중 하나는 골라야 합니다." : null },
            { "delay", r => 0.001 <= r.Sec && r.Sec <= 86400 && 0 <= r.Rand && r.Rand <= 100 ? null : "시간은 0.001~86400초, 랜덤은 0~100% 입니다." },
            { "color", r => new[] { r.R, r.G, r.B, r.Tol }.All(v => 0 <= v && v <= 255) ? null : "색과 편차는 0~255 입니다." },
            { "text", r => r.S != "" ? null : "글을 입력하세요." },
        };

        public static string Key(int vk)
        {
            string s;
            return VK.TryGetValue(vk, out s) ? s : "키" + vk;
        }

        public static string Hk(int[] hk)
        {
            if (hk == null) return "(없음)";
            var sb = new StringBuilder();
            if ((hk[1] & 1) != 0) sb.Append("Ctrl+");
            if ((hk[1] & 2) != 0) sb.Append("Alt+");
            if ((hk[1] & 4) != 0) sb.Append("Shift+");
            return sb + Key(hk[0]);
        }

        public static string Describe(Ev e)
        {
            switch (e.T)
            {
                case "key": return "키보드  " + Key(e.Vk) + "  " + Act[e.Act];
                case "mouse":
                    string s = "마우스";
                    if (e.Move != "none") s += "  " + (e.Move == "abs" ? "절대" : "상대") + "(" + e.X + ", " + e.Y + ")";
                    if (e.Btn != "none") s += "  " + Btn[e.Btn] + " " + Act[e.Act];
                    return s + (e.Wheel != 0 ? "  휠 " + e.Wheel : "");
                case "delay": return "지연  " + e.Sec.ToString("0.000", Num.Inv) + "초" + (e.Rand != 0 ? "  ±" + e.Rand + "%" : "");
                case "color": return "색 대기  (" + e.R + ", " + e.G + ", " + e.B + ") ±" + e.Tol + "  위치(" + e.X + ", " + e.Y + ")";
                case "text": return "문자열  " + e.S.Replace("\r", "").Replace("\n", "⏎");
                default: return "매크로 호출  '" + e.Name + "'";
            }
        }

        // 기록한 (시각, 이벤트) 목록 → 매크로 이벤트. 누르기+떼기는 합치고, 긴 간격은 지연으로 넣는다.
        public static List<Ev> Build(List<Tuple<double, Ev>> raw, double? minDelay, bool merge)
        {
            var output = new List<Ev>();
            double? end = null;
            for (int i = 0; i < raw.Count; i++)
            {
                double t = raw[i].Item1, tEnd = t;
                Ev e = raw[i].Item2;
                if (merge && e.Act == "down" && i + 1 < raw.Count)
                {
                    var up = e.Clone();
                    up.Act = "up";
                    if (raw[i + 1].Item2.Json() == up.Json())
                    {
                        e = e.Clone();
                        e.Act = "click";
                        tEnd = raw[i + 1].Item1;
                        i++;
                    }
                }
                if (minDelay != null && end != null && t - end.Value >= minDelay.Value)
                    output.Add(new Ev { T = "delay", Sec = Math.Round(t - end.Value, 3), Rand = 0 });
                output.Add(e);
                end = tEnd;
            }
            return output;
        }
    }

    class Field
    {
        public string Key, Label, Kind;
        public Dictionary<string, string> Choices;
        public Field(string key, string label, string kind) { Key = key; Label = label; Kind = kind; }
        public Field(string key, string label, Dictionary<string, string> choices) : this(key, label, "choice") { Choices = choices; }
    }

    // ───────────────────────── 실행 ─────────────────────────
    class Runner
    {
        const double Gap = 0.01;   // 이벤트 사이 기본 간격(초)
        readonly Macro macro;
        readonly Func<string, Macro> find;
        readonly HashSet<string> held = new HashSet<string>();   // "k65", "mleft" 처럼 지금 누르고 있는 것
        readonly Random rnd = new Random();
        readonly Thread th;
        public readonly string Key;
        public readonly ManualResetEvent StopEv = new ManualResetEvent(false);
        public Action<Runner> Done;

        public Runner(Macro m, Func<string, Macro> find, string key)
        {
            macro = m.Clone();
            this.find = find;
            Key = key;
            th = new Thread(Run) { IsBackground = true };
        }

        public void Start() { th.Start(); }
        public void Stop() { StopEv.Set(); }
        public bool Join(int ms) { return th.Join(ms); }
        bool Stopped { get { return StopEv.WaitOne(0); } }
        bool Wait(double sec) { return StopEv.WaitOne(TimeSpan.FromSeconds(Math.Max(0, sec))); }

        void Run()
        {
            try
            {
                for (int n = 0; !Stopped && (macro.Repeat == 0 || n < macro.Repeat); n++) Play(macro, 0);
            }
            finally
            {
                foreach (var what in held.ToList()) Emit(what, true);   // 중지 시 눌린 키·버튼을 모두 뗀다
                var d = Done;
                if (d != null) d(this);
            }
        }

        void Play(Macro m, int depth)
        {
            foreach (var e in m.Events)
            {
                if (Stopped) return;
                Do(e, depth);
                Wait(Gap);
            }
        }

        void Emit(string what, bool up)
        {
            string v = what.Substring(1);
            if (what[0] == 'k') Input.Key(int.Parse(v), up);
            else Input.Mouse(Input.BtnFlag[v][up ? 1 : 0], 0);
            if (up) held.Remove(what);
            else held.Add(what);
        }

        void Press(string what, string act)
        {
            if (act != "up") Emit(what, false);
            if (act == "click") Wait(0.02);
            if (act != "down") Emit(what, true);
        }

        void Do(Ev e, int depth)
        {
            switch (e.T)
            {
                case "key": Press("k" + e.Vk, e.Act); break;
                case "mouse":
                    if (e.Move != "none")
                    {
                        int[] c = e.Move == "abs" ? new[] { 0, 0 } : Input.Cursor();
                        W.SetCursorPos(c[0] + e.X, c[1] + e.Y);
                        Thread.Sleep(10);
                    }
                    if (e.Btn != "none") Press("m" + e.Btn, e.Act);
                    if (e.Wheel != 0) Input.Mouse(0x800, e.Wheel);
                    break;
                case "delay":
                    Wait(e.Sec * (1 + (rnd.NextDouble() * 2 - 1) * e.Rand / 100));
                    break;
                case "color":
                    while (!Stopped)
                    {
                        int[] p = Input.Pixel(e.X, e.Y);
                        if (p != null && Math.Abs(p[0] - e.R) <= e.Tol && Math.Abs(p[1] - e.G) <= e.Tol && Math.Abs(p[2] - e.B) <= e.Tol) break;
                        Wait(0.05);
                    }
                    break;
                case "text": Input.Text(e.S, Wait); break;
                case "call":
                    var m = find(e.Name);
                    if (depth >= 20) Stop();   // 서로 부르는 매크로(순환 호출) 방지
                    else if (m != null)
                        for (int i = 0; i < Math.Max(1, m.Repeat); i++) Play(m, depth + 1);
                    break;
            }
        }
    }
}
