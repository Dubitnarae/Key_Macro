// 키보드·마우스 매크로 (Windows 전용). 실행: Key_Macro.exe, 자체 시험: Key_Macro.exe --selftest (빌드 때 실행)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("키보드·마우스 매크로")]
[assembly: System.Reflection.AssemblyProduct("키보드·마우스 매크로")]
[assembly: System.Reflection.AssemblyCompany("KIM YEONGDO")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KIM YEONGDO")]
[assembly: System.Reflection.AssemblyVersion("0.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.2.0.0")]

namespace KeyMacro
{
    static class App
    {
        public const string Name = "키보드·마우스 매크로";
        public const string Author = "KIM YEONGDO";
        public const string Repo = "Dubitnarae/Key_Macro";   // 자동 업데이트가 릴리스를 확인하는 저장소(공개여야 함)

        public static string Version
        {
            get { var v = typeof(App).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 화면 배율과 상관없이 실제 픽셀 좌표를 쓴다
            try { W.SetProcessDpiAwareness(2); }
            catch (Exception) { try { W.SetProcessDPIAware(); } catch (Exception) { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--selftest")) return SelfTest.Run();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.ToString(), App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Updater.CleanupOld();
            string path = Path.Combine(App.ExeDir, "매크로.json"), warn;
            Data data;
            try { data = Store.Load(path, out warn); }
            catch (Exception e)
            {
                MessageBox.Show("매크로 파일을 열 수 없습니다:\n" + e.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            if (warn != null) MessageBox.Show(warn, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            var win = new MainForm(path, data);
            Application.Run(win);
            if (win.RestartAfterUpdate)
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, WorkingDirectory = App.ExeDir });
            return 0;
        }
    }

    // 자체 시험 — 실패하면 build.ps1 이 빌드를 막는다
    static class SelfTest
    {
        static int fails;
        static readonly StringBuilder log = new StringBuilder();

        static void Check(bool ok, string what)
        {
            if (!ok) fails++;
            log.AppendLine((ok ? "  ok   " : "  FAIL ") + what);
        }

        static Ev K(string act, int vk = 65) { return new Ev { T = "key", Vk = vk, Act = act }; }
        static string J(IEnumerable<Ev> es) { return string.Join("|", es.Select(e => e.Json())); }

        public static int Run()
        {
            var tests = new Dictionary<string, Action>
            {
                { "기록 정리", BuildTest }, { "이름·설명", NamesTest }, { "저장 형식", StoreTest }, { "입력 API", InputTest },
                { "실행기", RunnerTest }, { "화면", UiTest }, { "업데이트", UpdaterTest },
            };
            foreach (var t in tests)
            {
                log.AppendLine("[" + t.Key + "]");
                try { t.Value(); }
                catch (Exception e) { Check(false, "예외: " + e); }
            }
            log.AppendLine(fails == 0 ? "SELFTEST OK" : "SELFTEST FAILED: " + fails);
            var bytes = Encoding.UTF8.GetBytes(log.ToString());   // 창 프로그램이라 콘솔 인코딩을 못 바꾼다 — 바이트로 직접
            using (var o = Console.OpenStandardOutput()) o.Write(bytes, 0, bytes.Length);
            return fails == 0 ? 0 : 1;
        }

        static void BuildTest()
        {
            var raw = new List<Tuple<double, Ev>>
            {
                Tuple.Create(0.0, K("down")), Tuple.Create(0.05, K("up")), Tuple.Create(1.0, K("down", 160)),
                Tuple.Create(1.2, K("down", 66)), Tuple.Create(1.25, K("up", 66)), Tuple.Create(1.3, K("up", 160)),
            };
            var want = new[] { K("click"), new Ev { T = "delay", Sec = 0.95, Rand = 0 }, K("down", 160), K("click", 66), K("up", 160) };
            Check(J(Names.Build(raw, 0.5, true)) == J(want), "누르기+떼기 합치기, 긴 간격 지연");
            Check(J(Names.Build(raw, null, false)) == J(raw.Select(r => r.Item2)), "합치지 않기");
        }

        static void NamesTest()
        {
            Check(Names.Hk(new[] { 116, 3 }) == "Ctrl+Alt+F5" && Names.Hk(null) == "(없음)", "단축키 이름");
            Check(Names.Key(48) == "0" && Names.Key(96) == "Num0" && Names.Key(1000) == "키1000", "키 이름");
            foreach (var t in Names.Fields.Keys) Check(Names.Describe(Ev.New(t)) != "", "설명 " + t);
            Check(Names.Describe(new Ev { T = "text", S = "가\nb" }) == "문자열  가⏎b", "문자열 줄바꿈 표시");
            Check(Names.Describe(new Ev { T = "delay", Sec = 1, Rand = 30 }) == "지연  1.000초  ±30%", "지연 설명");
            Check(Names.Check["delay"](new Ev { Sec = 0 }) != null && Names.Check["delay"](new Ev { Sec = 1, Rand = 30 }) == null, "지연 범위 검사");
            Check(Names.Check["mouse"](new Ev { Move = "none", Btn = "none" }) != null, "빈 마우스 이벤트 거절");
        }

        // 파이썬판이 쓴 매크로.json 을 그대로 읽고, 다시 쓴 것도 같은 내용이어야 한다
        static void StoreTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "key_macro_selftest");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "매크로.json");
            File.WriteAllText(p, "{\n \"settings\": {\"toggle\": [119, 0], \"record\": [120, 1], \"capture\": [118, 0]},\n \"macros\": [{\"name\": \"가\", \"repeat\": 2, \"start\": [65, 1], \"stop\": null, \"events\": [" +
                "{\"t\": \"key\", \"vk\": 65, \"act\": \"click\"}, {\"t\": \"mouse\", \"move\": \"rel\", \"x\": -3, \"y\": 4, \"btn\": \"none\", \"act\": \"click\", \"wheel\": -120}," +
                "{\"t\": \"delay\", \"sec\": 1.5, \"rand\": 10}, {\"t\": \"delay\", \"sec\": 2, \"rand\": 0}, {\"t\": \"color\", \"x\": 1, \"y\": 2, \"r\": 3, \"g\": 4, \"b\": 5, \"tol\": 6}," +
                "{\"t\": \"text\", \"s\": \"한글\\nabc\"}, {\"t\": \"call\", \"name\": \"나\"}]}]}", new UTF8Encoding(false));
            string warn;
            var d = Store.Load(p, out warn);
            var m = d.Macros[0];
            Check(warn == null && d.Macros.Count == 1 && m.Name == "가" && m.Repeat == 2 && Num.HkEq(m.Start, new[] { 65, 1 }) && m.Stop == null, "파이썬판 파일 읽기");
            Check(Num.HkEq(d.Settings["record"], new[] { 120, 1 }) && m.Events.Count == 7 && m.Events[2].Sec == 1.5 && m.Events[3].Sec == 2 &&
                m.Events[1].Wheel == -120 && m.Events[5].S == "한글\nabc" && m.Events[6].Name == "나", "이벤트 값");
            string before = J(m.Events);
            Store.Save(p, d);
            var d2 = Store.Load(p, out warn);
            Check(warn == null && J(d2.Macros[0].Events) == before && Num.HkEq(d2.Settings["record"], new[] { 120, 1 }), "저장 후 다시 읽기");
            Check(File.ReadAllBytes(p)[0] == (byte)'{', "BOM 없음");
            File.WriteAllText(p, "{깨진 파일");
            var d3 = Store.Load(p, out warn);
            Check(warn != null && d3.Macros.Count == 0 && !File.Exists(p) && Directory.GetFiles(dir, "매크로.깨짐_*.json").Length == 1, "깨진 파일은 따로 옮김");
            Directory.Delete(dir, true);
        }

        static void InputTest()
        {
            Check(Marshal.SizeOf(typeof(W.INPUT)) == (IntPtr.Size == 8 ? 40 : 28), "INPUT 크기");
            Check(Input.Mouse(1, 0) == 1, "입력 보내기 (0칸 이동)");
            var c = Input.Cursor();
            Check(Input.Pixel(c[0], c[1]) != null, "화면 색 읽기");
            Check(Hook.Start(), "키보드·마우스 훅 설치");
        }

        static Macro M(string name, int repeat, params Ev[] es) { return new Macro { Name = name, Repeat = repeat, Events = es.ToList() }; }

        static void RunnerTest()
        {
            var sub = M("s", 2, new Ev { T = "delay", Sec = 0.01, Rand = 50 });
            var main = M("m", 2, new Ev { T = "call", Name = "s" }, new Ev { T = "call", Name = "없음" });
            bool done = false;
            var r = new Runner(main, n => n == "s" ? sub : null, "m") { Done = x => done = true };
            r.Start();
            Check(r.Join(5000) && done, "호출·반복 후 끝남");
            Macro loop = null;
            loop = M("l", 0, new Ev { T = "call", Name = "l" });
            r = new Runner(loop, n => loop, "l");   // 자기 자신을 부르는 매크로는 스스로 멈춘다
            r.Start();
            Check(r.Join(5000), "순환 호출 멈춤");
            r = new Runner(M("w", 0, new Ev { T = "delay", Sec = 60 }), n => null, "w");
            r.Start();
            Thread.Sleep(100);
            r.Stop();
            Check(r.Join(2000), "긴 지연 중 중지");
        }

        static void UiTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "key_macro_selftest_ui");
            Directory.CreateDirectory(dir);
            var data = new Data();
            var all = Names.Fields.Keys.Select(Ev.New).ToArray();
            data.Macros.Add(M("m", 1, all));
            using (var app = new MainForm(Path.Combine(dir, "t.json"), data))
            {
                var h = app.Handle;
                foreach (var f in new Form[] { new MacroDialog(app, data.Macros[0], new List<string>()), new RecordDialog(app),
                    new InputForm(app, "t", Names.Fields["mouse"].Item2, Ev.New("mouse").ToDict(), null),
                    new InputForm(app, "t", Names.Fields["color"].Item2, Ev.New("color").ToDict(), null) })
                {
                    h = f.Handle;
                    f.Dispose();
                }
                Check(true, "창 만들기");
                app.OnKey(119, true, 0);   // F8 → 실행 가능
                Check(app.RunMode, "F8 로 실행 가능");
                app.OnKey(119, false, 0);
                app.OnKey(119, true, 0);
                Check(!app.RunMode, "F8 다시 누르면 중단");
            }
            Directory.Delete(dir, true);
        }

        static void UpdaterTest()
        {
            Check(Updater.IsNewer("0.3.0", "0.2.0") && Updater.IsNewer("v0.2.1", "0.2.0") && Updater.IsNewer("1.0", "0.9.9"), "새 버전");
            Check(!Updater.IsNewer("0.2.0", "0.2.0") && !Updater.IsNewer("0.1.9", "0.2.0"), "같거나 옛 버전");
            var abc = Encoding.ASCII.GetBytes("abc");
            string h = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            Check(Updater.Verified(abc, h + "  Key_Macro.exe\n"), "맞는 해시");
            Check(!Updater.Verified(Encoding.ASCII.GetBytes("abd"), h) && !Updater.Verified(abc, ""), "틀린 해시·빈 값");
        }
    }
}
