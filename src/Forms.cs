// 화면: 메인 창, 매크로 편집 창, 기록 창, 입력 창

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeyMacro
{
    static class Ui
    {
        public static readonly Font Font = SystemFonts.MessageBoxFont;
        public static readonly float Scale = GetScale();

        static float GetScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static Button Btn(string text, Action click, int width = 0)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(S(2)) };
            if (width > 0) b.MinimumSize = new Size(S(width), 0);
            b.Click += (s, e) => click();
            return b;
        }

        public static Label Lbl(string text, Color? color = null)
        {
            var l = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(4)) };
            if (color != null) l.ForeColor = color.Value;
            return l;
        }

        public static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        public static FlowLayoutPanel Column(params Control[] cs)
        {
            var p = Row(cs);
            p.FlowDirection = FlowDirection.TopDown;
            return p;
        }

        public static TableLayoutPanel Grid(int cols)
        {
            return new TableLayoutPanel { AutoSize = true, ColumnCount = cols, Margin = new Padding(S(6)) };
        }

        public static void Error(IWin32Window owner, string msg)
        {
            MessageBox.Show(owner, msg, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    class Dialog : Form
    {
        public Dialog(string title)
        {
            Text = title;
            Font = Ui.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(Ui.S(4));
        }
    }

    // 버튼을 누른 뒤 키를 누르면 그 키가 지정된다. 값은 [가상키, 조합키] 또는 null.
    class KeyPick : FlowLayoutPanel
    {
        readonly MainForm app;
        readonly bool allowMod;
        readonly Button btn;
        public int[] Value;

        public KeyPick(MainForm app, int[] value, bool allowMod = false)
        {
            this.app = app;
            this.allowMod = allowMod;
            AutoSize = true;
            WrapContents = false;
            Margin = new Padding(0);
            btn = Ui.Btn("", Pick, 150);
            Controls.Add(btn);
            if (!allowMod) Controls.Add(Ui.Btn("지우기", () => Set(null)));
            Set(value);
        }

        public void Set(int[] v)
        {
            if (IsDisposed) return;   // 창이 이미 닫힘
            Value = v;
            btn.Text = Names.Hk(v);
        }

        void Pick()
        {
            btn.Text = "키를 누르세요…";
            var f = FindForm();
            if (f != null) f.ActiveControl = null;   // Space·Enter 가 이 버튼을 다시 누르지 않게
            app.KeyCapture = Set;
            app.CaptureAllowMod = allowMod;
        }
    }

    // 입력 칸 목록으로 만드는 창. 결과는 칸 이름 → 값.
    class InputForm : Dialog
    {
        readonly MainForm app;
        readonly Func<Dictionary<string, object>, string> check;
        readonly Dictionary<string, Func<object>> get = new Dictionary<string, Func<object>>();
        readonly Dictionary<string, TextBox> box = new Dictionary<string, TextBox>();
        public Dictionary<string, object> Result;

        public InputForm(MainForm app, string title, Field[] fields, Dictionary<string, object> init, Func<Dictionary<string, object>, string> check)
            : base(title)
        {
            this.app = app;
            this.check = check;
            var g = Ui.Grid(2);
            foreach (var f0 in fields)
            {
                var f = f0;
                object v;
                init.TryGetValue(f.Key, out v);
                Control w;
                if (f.Kind == "choice")
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(160) };
                    cb.Items.AddRange(f.Choices.Values.ToArray());
                    string label;
                    cb.SelectedItem = v is string && f.Choices.TryGetValue((string)v, out label) ? label : f.Choices.Values.First();
                    get[f.Key] = () => f.Choices.First(kv => kv.Value == (string)cb.SelectedItem).Key;
                    w = cb;
                }
                else if (f.Kind == "key")
                {
                    int vk = v == null ? 0 : Num.I(v);
                    var kp = new KeyPick(app, vk != 0 ? new[] { vk, 0 } : null, true);
                    get[f.Key] = () => { if (kp.Value == null) throw new FormatException(); return kp.Value[0]; };
                    w = kp;
                }
                else if (f.Kind == "hotkey")
                {
                    var kp = new KeyPick(app, v as int[]);
                    get[f.Key] = () => { if (kp.Value == null) throw new FormatException(); return kp.Value; };
                    w = kp;
                }
                else if (f.Kind == "text")
                {
                    var tb = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
                        Width = Ui.S(300), Height = Ui.S(90), Text = ((v as string) ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") };
                    get[f.Key] = () => tb.Text.Replace("\r\n", "\n");
                    w = tb;
                }
                else
                {
                    var tb = box[f.Key] = new TextBox { Width = Ui.S(160), Text = v == null ? "" : Convert.ToString(v, Num.Inv) };
                    if (f.Kind == "int") get[f.Key] = () => int.Parse(tb.Text.Trim(), Num.Inv);
                    else get[f.Key] = () => double.Parse(tb.Text.Trim(), System.Globalization.NumberStyles.Float, Num.Inv);
                    w = tb;
                }
                w.Margin = new Padding(Ui.S(4));
                var l = Ui.Lbl(f.Label);
                l.Anchor = AnchorStyles.Right;
                g.Controls.Add(l);
                g.Controls.Add(w);
            }
            if (box.ContainsKey("x"))
            {
                string what = box.ContainsKey("r") ? "마우스 위치와 그 자리 색" : "마우스 위치";
                var hint = Ui.Lbl(Names.Hk(app.Data.Settings["capture"]) + " 키: 현재 " + what + " 가져오기", Color.FromArgb(0, 0x66, 0xcc));
                g.Controls.Add(hint);
                g.SetColumnSpan(hint, 2);
                app.PosCb = Fill;
                FormClosed += (s, e) => { if (app.PosCb == Fill) app.PosCb = null; };
            }
            var buttons = Ui.Row(Ui.Btn("확인", Ok, 70), Ui.Btn("취소", Close, 70));
            buttons.Anchor = AnchorStyles.None;
            g.Controls.Add(buttons);
            g.SetColumnSpan(buttons, 2);
            Controls.Add(g);
        }

        public void Fill(int x, int y, int[] rgb)
        {
            var vals = new[] { x, y, rgb[0], rgb[1], rgb[2] };
            string keys = "xyrgb";
            for (int i = 0; i < 5; i++)
            {
                TextBox tb;
                if (box.TryGetValue(keys[i].ToString(), out tb)) tb.Text = vals[i].ToString(Num.Inv);
            }
        }

        void Ok()
        {
            Dictionary<string, object> res;
            try { res = get.ToDictionary(kv => kv.Key, kv => kv.Value()); }
            catch (FormatException) { Ui.Error(this, "입력 값을 확인하세요."); return; }
            catch (OverflowException) { Ui.Error(this, "입력 값을 확인하세요."); return; }
            string err = check == null ? null : check(res);
            if (err != null) { Ui.Error(this, err); return; }
            Result = res;
            Close();
        }

        // 이벤트 하나를 묻는다. 취소하면 null.
        public static Ev AskEvent(IWin32Window owner, MainForm app, Ev e, List<string> others)
        {
            var spec = Names.Fields[e.T];
            Field[] fields = spec.Item2;
            if (e.T == "call")
            {
                if (others.Count == 0)
                {
                    MessageBox.Show(owner, "호출할 다른 매크로가 없습니다.", App.Name);
                    return null;
                }
                fields = new[] { new Field("name", "호출할 매크로", others.ToDictionary(n => n, n => n)) };
            }
            Func<Ev, string> chk;
            Names.Check.TryGetValue(e.T, out chk);
            Func<Dictionary<string, object>, string> check = r => chk == null ? null : chk(ToEv(e.T, r));
            var f = new InputForm(app, spec.Item1 + " 이벤트", fields, e.ToDict(), check);
            app.ShowModal(f, owner);
            return f.Result == null ? null : ToEv(e.T, f.Result);
        }

        static Ev ToEv(string t, Dictionary<string, object> r)
        {
            var d = new Dictionary<string, object>(r);
            d["t"] = t;
            return Ev.FromDict(d);
        }
    }

    class MacroDialog : Dialog
    {
        readonly MainForm app;
        readonly List<string> others;
        readonly TextBox name, repeat;
        readonly ListBox lb;
        readonly KeyPick start, stop;
        public readonly Macro M;
        public Macro Result;

        public MacroDialog(MainForm app, Macro macro, List<string> others) : base("매크로 편집")
        {
            this.app = app;
            this.others = others;
            M = macro.Clone();
            var g = Ui.Grid(2);
            name = new TextBox { Width = Ui.S(220), Text = M.Name };
            var top = Ui.Row(Ui.Lbl("매크로 이름:"), name);
            g.Controls.Add(top);
            g.SetColumnSpan(top, 2);
            lb = new ListBox { Width = Ui.S(380), Height = Ui.S(300), IntegralHeight = false, HorizontalScrollbar = true };
            lb.DoubleClick += (s, e) => Edit();
            g.Controls.Add(lb);
            var side = Ui.Column();
            foreach (var kv in Names.Fields)
            {
                string t = kv.Key;
                side.Controls.Add(Ui.Btn(kv.Value.Item1 + " 추가", () => Add(t), 110));
            }
            var edit = Ui.Btn("편집", Edit, 110);
            edit.Margin = new Padding(Ui.S(2), Ui.S(12), Ui.S(2), Ui.S(2));
            side.Controls.AddRange(new Control[] { edit, Ui.Btn("삭제", Delete, 110), Ui.Btn("▲ 위로", () => MoveSel(-1), 110), Ui.Btn("▼ 아래로", () => MoveSel(1), 110) });
            g.Controls.Add(side);
            var opt = Ui.Grid(2);
            opt.Margin = new Padding(0);
            repeat = new TextBox { Width = Ui.S(70), Text = M.Repeat.ToString(Num.Inv) };
            start = new KeyPick(app, M.Start);
            stop = new KeyPick(app, M.Stop);
            foreach (var row in new[] { Tuple.Create("반복 횟수 (0이면 무한):", (Control)repeat), Tuple.Create("시작 단축키:", (Control)start),
                Tuple.Create("중지 단축키 (없으면 시작 키로 중지):", (Control)stop) })
            {
                var l = Ui.Lbl(row.Item1);
                l.Anchor = AnchorStyles.Right;
                opt.Controls.Add(l);
                opt.Controls.Add(row.Item2);
            }
            g.Controls.Add(opt);
            g.SetColumnSpan(opt, 2);
            var stopBtn = Ui.Btn("중단", () => app.StopRun(MainForm.TestKey), 70);
            stopBtn.Margin = new Padding(Ui.S(2), Ui.S(2), Ui.S(30), Ui.S(2));
            var buttons = Ui.Row(Ui.Btn("시험 실행", Test, 70), stopBtn, Ui.Btn("확인", Ok, 70), Ui.Btn("취소", Close, 70));
            buttons.Anchor = AnchorStyles.None;
            g.Controls.Add(buttons);
            g.SetColumnSpan(buttons, 2);
            Controls.Add(g);
            FormClosed += (s, e) => app.StopRun(MainForm.TestKey);
            FillList(null);
        }

        void FillList(int? sel)
        {
            lb.BeginUpdate();
            lb.Items.Clear();
            foreach (var e in M.Events) lb.Items.Add(Names.Describe(e));
            if (sel != null && M.Events.Count > 0) lb.SelectedIndex = sel.Value;
            lb.EndUpdate();
        }

        int? Sel { get { return lb.SelectedIndex < 0 ? (int?)null : lb.SelectedIndex; } }

        void Add(string t)
        {
            var e = InputForm.AskEvent(this, app, Ev.New(t), others);
            if (e == null) return;
            int i = Sel == null ? M.Events.Count : Sel.Value + 1;
            M.Events.Insert(i, e);
            FillList(i);
        }

        void Edit()
        {
            if (Sel == null) return;
            int i = Sel.Value;
            var e = InputForm.AskEvent(this, app, M.Events[i], others);
            if (e == null) return;
            M.Events[i] = e;
            FillList(i);
        }

        void Delete()
        {
            if (Sel == null) return;
            int i = Sel.Value;
            M.Events.RemoveAt(i);
            FillList(Math.Min(i, M.Events.Count - 1));
        }

        void MoveSel(int d)
        {
            if (Sel == null) return;
            int i = Sel.Value;
            if (i + d < 0 || i + d >= M.Events.Count) return;
            var t = M.Events[i];
            M.Events[i] = M.Events[i + d];
            M.Events[i + d] = t;
            FillList(i + d);
        }

        string Collect()
        {
            string n = name.Text.Trim();
            if (n == "") return "매크로 이름을 입력하세요.";
            if (others.Contains(n)) return "같은 이름의 매크로가 있습니다.";
            int r;
            if (!int.TryParse(repeat.Text.Trim(), System.Globalization.NumberStyles.None, Num.Inv, out r)) return "반복 횟수는 0 이상의 숫자입니다.";
            M.Name = n;
            M.Repeat = r;
            M.Start = start.Value;
            M.Stop = stop.Value;
            return null;
        }

        void Test()
        {
            string err = Collect();
            if (err != null) Ui.Error(this, err);
            else app.StartRun(M, MainForm.TestKey);
        }

        void Ok()
        {
            string err = Collect();
            if (err != null) { Ui.Error(this, err); return; }
            Result = M;
            Close();
        }
    }

    class RecordDialog : Dialog
    {
        readonly MainForm app;
        readonly string keyName;
        readonly Label state;
        readonly Dictionary<string, CheckBox> opt = new Dictionary<string, CheckBox>();
        readonly TextBox px, ms;
        readonly List<Tuple<double, Ev>> raw = new List<Tuple<double, Ev>>();
        int nPx, nMs;
        int[] last;
        public bool On;
        public List<Ev> Result;

        public RecordDialog(MainForm app) : base("매크로 기록")
        {
            this.app = app;
            keyName = Names.Hk(app.Data.Settings["record"]);
            var g = Ui.Grid(3);
            state = Ui.Lbl(keyName + " 키를 누르면 기록을 시작합니다.", Color.FromArgb(0xcc, 0, 0));
            state.Anchor = AnchorStyles.None;
            state.Margin = new Padding(Ui.S(10), Ui.S(8), Ui.S(10), Ui.S(8));
            g.Controls.Add(state);
            g.SetColumnSpan(state, 3);
            px = new TextBox { Width = Ui.S(50), Text = "20" };
            ms = new TextBox { Width = Ui.S(50), Text = "50" };
            var rows = new[]
            {
                Tuple.Create("kb", "키보드 키", true, (TextBox)null, ""), Tuple.Create("btn", "마우스 버튼 (위치 포함)", true, (TextBox)null, ""),
                Tuple.Create("move", "마우스 움직임:", false, px, "픽셀 이상 움직일 때"), Tuple.Create("wheel", "마우스 휠", true, (TextBox)null, ""),
                Tuple.Create("delay", "시간 지연:", true, ms, "밀리초 이상일 때"), Tuple.Create("merge", "누르기+떼기를 하나로 합치기", true, (TextBox)null, ""),
            };
            foreach (var r in rows)
            {
                var cb = opt[r.Item1] = new CheckBox { Text = r.Item2, Checked = r.Item3, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(10), Ui.S(2), Ui.S(2), Ui.S(2)) };
                g.Controls.Add(cb);
                if (r.Item4 != null)
                {
                    g.Controls.Add(r.Item4);
                    g.Controls.Add(Ui.Lbl(r.Item5));
                }
                else g.SetColumnSpan(cb, 3);
            }
            var cancel = Ui.Btn("취소", Close, 70);
            cancel.Anchor = AnchorStyles.None;
            g.Controls.Add(cancel);
            g.SetColumnSpan(cancel, 3);
            Controls.Add(g);
            FormClosed += (s, e) => { On = Hook.RecOn = false; app.Rec = null; };
        }

        public bool Opt(string k) { return opt[k].Checked; }

        public void Toggle()
        {
            if (On)
            {
                On = Hook.RecOn = false;
                Result = Names.Build(raw, Opt("delay") ? nMs / 1000.0 : (double?)null, Opt("merge"));
                Close();
                return;
            }
            if (!int.TryParse(px.Text, System.Globalization.NumberStyles.None, Num.Inv, out nPx) ||
                !int.TryParse(ms.Text, System.Globalization.NumberStyles.None, Num.Inv, out nMs))
            {
                state.Text = "픽셀·밀리초 숫자를 확인하세요.";
                return;
            }
            last = Input.Cursor();
            On = Hook.RecOn = true;
            Add(0, null);
        }

        public void Add(double t, Ev e)
        {
            if (e != null) raw.Add(Tuple.Create(t, e));
            state.Text = "기록 중… " + raw.Count + "개  (" + keyName + " 키로 끝내기)";
        }

        public void Mouse(int msg, int x, int y, int delta, double t)
        {
            var e = new Ev { T = "mouse", Move = "abs", X = x, Y = y, Btn = "none", Act = "click", Wheel = 0 };
            if (msg == 0x200)
            {
                if (Opt("move") && Math.Max(Math.Abs(x - last[0]), Math.Abs(y - last[1])) >= nPx)
                {
                    last = new[] { x, y };
                    Add(t, e);
                }
            }
            else if (msg == 0x20A)
            {
                if (Opt("wheel"))
                {
                    e.Move = "none"; e.X = e.Y = 0; e.Wheel = delta;
                    Add(t, e);
                }
            }
            else if (MB.ContainsKey(msg) && Opt("btn"))
            {
                e.Btn = MB[msg][0];
                e.Act = MB[msg][1];
                Add(t, e);
            }
        }

        static readonly Dictionary<int, string[]> MB = new Dictionary<int, string[]>
        {
            { 0x201, new[] { "left", "down" } }, { 0x202, new[] { "left", "up" } }, { 0x204, new[] { "right", "down" } },
            { 0x205, new[] { "right", "up" } }, { 0x207, new[] { "middle", "down" } }, { 0x208, new[] { "middle", "up" } },
        };
    }

    class MainForm : Form
    {
        public const string TestKey = "\0test";
        readonly string path;
        public readonly Data Data;
        readonly Dictionary<string, Runner> runners = new Dictionary<string, Runner>();
        readonly HashSet<int> held = new HashSet<int>();
        readonly RadioButton rbOn, rbOff;
        readonly Label hint;
        readonly ListBox lb;
        int modal;   // 떠 있는 편집 창 수
        public bool RunMode;
        public Action<int[]> KeyCapture;   // 키 지정 버튼이 기다리는 중
        public bool CaptureAllowMod;
        public Action<int, int, int[]> PosCb;   // 마우스 위치·색 가져오기
        public RecordDialog Rec;
        public bool RestartAfterUpdate;

        public MainForm(string path, Data data)
        {
            this.path = path;
            Data = data;
            Text = App.Name + " v" + App.Version;
            Font = Ui.Font;
            TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StartPosition = FormStartPosition.CenterScreen;
            var g = Ui.Grid(2);
            rbOn = new RadioButton { Text = "매크로 실행 가능", AutoSize = true };
            rbOff = new RadioButton { Text = "실행 중단과 편집", AutoSize = true, Checked = true, Margin = new Padding(Ui.S(10), Ui.S(3), Ui.S(3), Ui.S(3)) };
            rbOn.Click += (s, e) => SetMode(true);
            rbOff.Click += (s, e) => SetMode(false);
            var top = Ui.Row(rbOn, rbOff);
            g.Controls.Add(top);
            g.SetColumnSpan(top, 2);
            hint = Ui.Lbl("", Color.FromArgb(0, 0x66, 0xcc));
            g.Controls.Add(hint);
            g.SetColumnSpan(hint, 2);
            lb = new ListBox { Width = Ui.S(320), Height = Ui.S(250), IntegralHeight = false };
            lb.DoubleClick += (s, e) => Edit();
            g.Controls.Add(lb);
            var side = Ui.Column(Ui.Btn("기록", Record, 90), Ui.Btn("추가", () => Add(null), 90), Ui.Btn("편집", Edit, 90),
                Ui.Btn("복사", CopySel, 90), Ui.Btn("삭제", Delete, 90), Ui.Btn("▲ 위로", () => MoveSel(-1), 90),
                Ui.Btn("▼ 아래로", () => MoveSel(1), 90), Ui.Btn("실행 / 중지", RunSelected, 90), Ui.Btn("설정", Settings, 90));
            g.Controls.Add(side);
            var foot = Ui.Lbl("v" + App.Version + "  ·  개발자 " + App.Author, Color.Gray);
            foot.Anchor = AnchorStyles.Right;
            g.Controls.Add(foot);
            g.SetColumnSpan(foot, 2);
            Controls.Add(g);
            FormClosing += (s, e) => SetMode(false);
            RefreshList(null);
        }

        // 다른 스레드(훅·실행기·업데이트 확인)에서 화면 스레드로 넘긴다
        public void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }   // 창이 닫히는 중
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Hook.Key = (vk, down, t) => Post(() => OnKey(vk, down, t));
            Hook.Mouse = (msg, x, y, d, t) => Post(() => { if (Rec != null && Rec.On) Rec.Mouse(msg, x, y, d, t); });
            if (!Hook.Start())
                Ui.Error(this, "키보드·마우스 감시를 시작하지 못했습니다. 단축키와 기록이 동작하지 않습니다.");
            CheckUpdate();
        }

        public void ShowModal(Form f, IWin32Window owner)
        {
            modal++;
            try { f.ShowDialog(owner ?? this); }
            finally { modal--; f.Dispose(); KeyCapture = null; }   // 키 지정을 기다리다 닫힌 창이 다음 키를 먹지 않게
        }

        // ── 목록 ──
        List<Macro> Macros { get { return Data.Macros; } }
        Macro Find(string name) { return Macros.FirstOrDefault(m => m.Name == name); }
        int? Sel { get { return lb.SelectedIndex < 0 ? (int?)null : lb.SelectedIndex; } }

        public void RefreshList(int? sel)
        {
            if (sel == null) sel = Sel;
            lb.BeginUpdate();
            lb.Items.Clear();
            foreach (var m in Macros) lb.Items.Add((runners.ContainsKey(m.Name) ? "▶" : "    ") + " " + m.Name + "    [" + Names.Hk(m.Start) + "]");
            if (sel != null && sel.Value < Macros.Count) lb.SelectedIndex = sel.Value;
            lb.EndUpdate();
            hint.Text = Names.Hk(Data.Settings["toggle"]) + " 키: 실행 가능 ↔ 중단 전환 (급할 때 모두 멈춤)";
        }

        void Changed(int? sel)
        {
            Store.Save(path, Data);
            RefreshList(sel);
        }

        // 편집 창을 띄우고, 확인하면 목록에 넣거나(index 없음) 바꾼다
        void Open(Macro m, int? index)
        {
            SetMode(false);
            var others = Macros.Where((x, i) => i != index).Select(x => x.Name).ToList();
            var d = new MacroDialog(this, m, others);
            ShowModal(d, this);
            var r = d.Result;
            if (r == null) return;
            if (index == null)
            {
                Macros.Add(r);
                index = Macros.Count - 1;
            }
            else
            {
                foreach (var x in Macros)   // 이름이 바뀌면 이 매크로를 부르는 곳도 고친다
                    foreach (var e in x.Events)
                        if (e.T == "call" && e.Name == m.Name) e.Name = r.Name;
                Macros[index.Value] = r;
            }
            Changed(index);
        }

        string NewName(string b)
        {
            int n = 1;
            while (Find(b + " " + n) != null) n++;
            return b + " " + n;
        }

        void Add(List<Ev> events)
        {
            Open(new Macro { Name = NewName(events != null ? "기록" : "매크로"), Events = events ?? new List<Ev>() }, null);
        }

        void Edit()
        {
            if (Sel != null) Open(Macros[Sel.Value], Sel.Value);
        }

        void CopySel()
        {
            if (Sel == null) return;
            var m = Macros[Sel.Value].Clone();
            m.Name = NewName(m.Name + " 복사");
            m.Start = m.Stop = null;
            Macros.Add(m);
            Changed(Macros.Count - 1);
        }

        void Delete()
        {
            if (Sel == null) return;
            int i = Sel.Value;
            if (MessageBox.Show(this, "'" + Macros[i].Name + "' 매크로를 삭제할까요?", App.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            StopRun(Macros[i].Name);
            Macros.RemoveAt(i);
            Changed(null);
        }

        void MoveSel(int d)
        {
            if (Sel == null) return;
            int i = Sel.Value;
            if (i + d < 0 || i + d >= Macros.Count) return;
            var t = Macros[i];
            Macros[i] = Macros[i + d];
            Macros[i + d] = t;
            Changed(i + d);
        }

        void Record()
        {
            SetMode(false);
            var d = Rec = new RecordDialog(this);
            ShowModal(d, this);
            if (d.Result != null && d.Result.Count > 0) Add(d.Result);
        }

        void Settings()
        {
            SetMode(false);
            var fields = new[] { new Field("toggle", "실행 가능 ↔ 중단 전환 키", "hotkey"), new Field("record", "기록 시작/중지 키", "hotkey"),
                new Field("capture", "마우스 위치·색 가져오기 키", "hotkey") };
            var f = new InputForm(this, "설정", fields, Data.Settings.ToDictionary(kv => kv.Key, kv => (object)kv.Value), null);
            ShowModal(f, this);
            if (f.Result == null) return;
            foreach (var kv in f.Result) Data.Settings[kv.Key] = (int[])kv.Value;
            Changed(null);
        }

        // ── 실행 ──
        public void SetMode(bool on)
        {
            RunMode = on;
            rbOn.Checked = on;
            rbOff.Checked = !on;
            if (!on) foreach (var r in runners.Values) r.Stop();
        }

        public void StartRun(Macro m, string key = null)
        {
            key = key ?? m.Name;
            if (runners.ContainsKey(key)) return;
            var r = runners[key] = new Runner(m, Find, key);
            r.Done = x => Post(() =>
            {
                Runner cur;
                if (runners.TryGetValue(x.Key, out cur) && cur == x) runners.Remove(x.Key);
                RefreshList(null);
            });
            r.Start();
            RefreshList(null);
        }

        public void StopRun(string key)
        {
            Runner r;
            if (runners.TryGetValue(key, out r)) r.Stop();
        }

        public bool Running { get { return runners.Count > 0; } }

        void RunSelected()
        {
            if (Sel == null) return;
            var m = Macros[Sel.Value];
            if (runners.ContainsKey(m.Name)) StopRun(m.Name);
            else StartRun(m);
        }

        // ── 훅에서 온 입력 ──
        public void OnKey(int vk, bool down, double t)
        {
            if (!down) held.Remove(vk);
            else if (held.Contains(vk)) return;   // 누르고 있을 때 반복되는 입력
            else held.Add(vk);
            var hk = new[] { vk, Input.ModsNow() };
            var s = Data.Settings;
            if (KeyCapture != null)
            {
                if (down && (CaptureAllowMod || !Input.Mods.Contains(vk)))
                {
                    var cb = KeyCapture;
                    KeyCapture = null;
                    cb(new[] { vk, CaptureAllowMod ? 0 : hk[1] });
                }
                return;
            }
            if (Num.HkEq(hk, s["toggle"]))
            {
                if (down) SetMode(!(RunMode || Running) && modal == 0);   // 실행 중인 것이 있으면 무조건 멈춤. 편집 창이 떠 있으면 켜지 않는다
                return;
            }
            if (Rec != null)
            {
                if (Num.HkEq(hk, s["record"])) { if (down) Rec.Toggle(); }
                else if (Rec.On && Rec.Opt("kb")) Rec.Add(t, new Ev { T = "key", Vk = vk, Act = down ? "down" : "up" });
                return;
            }
            if (!down) return;
            if (PosCb != null && Num.HkEq(hk, s["capture"]))
            {
                var c = Input.Cursor();
                PosCb(c[0], c[1], Input.Pixel(c[0], c[1]) ?? new[] { 0, 0, 0 });
                return;
            }
            if (!RunMode) return;
            foreach (var m in Macros.ToList())
            {
                bool running = runners.ContainsKey(m.Name);
                if (Num.HkEq(hk, m.Start) && !running) StartRun(m);
                else if (running && Num.HkEq(hk, m.Stop ?? m.Start)) StopRun(m.Name);
            }
        }

        // ── 자동 업데이트: 시작할 때 한 번 백그라운드로 확인 ──
        void CheckUpdate()
        {
            Task.Run(() =>
            {
                try
                {
                    var found = Updater.Check();
                    if (found != null) Post(() => OnUpdate(found));
                }
                catch { }   // 인터넷이 없거나 릴리스가 없으면 조용히 넘어간다
            });
        }

        void OnUpdate(Updater.Found found)
        {
            if (modal > 0 || Running) return;   // 편집·실행 중에는 묻지 않는다
            string notes = found.Notes.Length > 600 ? found.Notes.Substring(0, 600) : found.Notes;
            if (MessageBox.Show(this, "새 버전 v" + found.Ver + " 이 있습니다. 지금 업데이트할까요?\n\n" + notes, App.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetMode(false);
            Cursor = Cursors.WaitCursor;
            try { Updater.Install(found); }
            catch (Exception e)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, e.Message, "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RestartAfterUpdate = true;
            Close();
        }
    }
}
