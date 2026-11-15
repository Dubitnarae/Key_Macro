// 자동 업데이트 — GitHub 최신 릴리스에서 Key_Macro.exe 와 Key_Macro.exe.sha256 을 받아 SHA256이 맞을 때만 교체한다.
// 토큰 없이 읽으므로 저장소가 공개여야 한다. 실행 중인 exe 는 이름 바꾸기는 되므로
// 현재 exe → Key_Macro.old, 새 exe → 원래 이름으로 바꾼 뒤 다시 실행한다. .old 는 다음 실행 때 지운다.
// (파이썬판 v0.1.0 도 같은 파일 이름·같은 .old 규칙이라 그대로 이 버전으로 넘어온다)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeyMacro
{
    static class Updater
    {
        const string ExeName = "Key_Macro.exe";

        public class Found
        {
            public string Ver, ExeUrl, ShaUrl, Notes;
        }

        public static bool IsNewer(string candidate, string current)
        {
            Func<string, List<int>> t = v => Regex.Matches(v, "[0-9]+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
            List<int> a = t(candidate), b = t(current);
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                int x = i < a.Count ? a[i] : 0, y = i < b.Count ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        public static bool Verified(byte[] data, string shaText)
        {
            string expected = shaText.Trim() == "" ? "" : shaText.Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$")) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant() == expected;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "Key_Macro/" + App.Version;
            return wc;
        }

        // 새 버전이 있으면 Found, 없으면 null. 실패는 예외.
        public static Found Check()
        {
            string json;
            using (var wc = Client()) json = wc.DownloadString("https://api.github.com/repos/" + App.Repo + "/releases/latest");
            var rel = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
            string ver = ((rel.ContainsKey("tag_name") ? rel["tag_name"] as string : null) ?? "").TrimStart('v', 'V');
            var assets = new Dictionary<string, string>();
            if (rel.ContainsKey("assets") && rel["assets"] is object[])
                foreach (var a in ((object[])rel["assets"]).OfType<Dictionary<string, object>>())
                    if (a.ContainsKey("name") && a.ContainsKey("browser_download_url"))
                        assets[(string)a["name"]] = (string)a["browser_download_url"];
            if (!IsNewer(ver, App.Version) || !assets.ContainsKey(ExeName) || !assets.ContainsKey(ExeName + ".sha256")) return null;
            return new Found
            {
                Ver = ver, ExeUrl = assets[ExeName], ShaUrl = assets[ExeName + ".sha256"],
                Notes = (rel.ContainsKey("body") ? rel["body"] as string : null) ?? "",
            };
        }

        // 내려받아 검증 후 교체한다. 새 exe 실행은 호출한 쪽이 창을 닫은 뒤 한다.
        public static void Install(Found f)
        {
            string cur = Application.ExecutablePath;
            byte[] data;
            string sha;
            using (var wc = Client())
            {
                data = wc.DownloadData(f.ExeUrl);
                sha = Encoding.ASCII.GetString(wc.DownloadData(f.ShaUrl));
            }
            if (!Verified(data, sha)) throw new InvalidDataException("내려받은 파일의 SHA256이 맞지 않아 설치하지 않았습니다.");
            string neu = Path.ChangeExtension(cur, ".new"), old = Path.ChangeExtension(cur, ".old");
            File.WriteAllBytes(neu, data);
            if (File.Exists(old)) File.Delete(old);
            File.Move(cur, old);
            try { File.Move(neu, cur); }
            catch { File.Move(old, cur); throw; }   // 되돌리기
        }

        // 지난 업데이트에서 남은 옛 exe 정리 (옛 프로그램이 끝날 때까지 몇 번 다시 시도)
        public static void CleanupOld()
        {
            string old = Path.ChangeExtension(Application.ExecutablePath, ".old");
            if (!File.Exists(old)) return;
            Task.Run(() =>
            {
                for (int i = 0; i < 20 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch (IOException) { Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(500); }
                }
            });
        }
    }
}
