"""키보드·마우스 매크로 (Windows 전용, 파이썬 표준 라이브러리만 사용)."""
import copy
import ctypes
import json
import queue
import random
import sys
import threading
import time
import tkinter as tk
import traceback
from ctypes import wintypes as W
from pathlib import Path
from tkinter import messagebox, ttk

import updater
from version import APP_NAME, AUTHOR, __version__

TITLE = APP_NAME
GAP = 0.01  # 이벤트 사이 기본 간격(초)

# ───────────────────────── 윈도우 API ─────────────────────────
u32, gdi, k32 = ctypes.WinDLL("user32"), ctypes.WinDLL("gdi32"), ctypes.WinDLL("kernel32")
PTR = ctypes.c_size_t
HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_int, W.WPARAM, W.LPARAM)
u32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, ctypes.c_void_p, W.DWORD]
u32.SetWindowsHookExW.restype = ctypes.c_void_p
u32.CallNextHookEx.argtypes = [ctypes.c_void_p, ctypes.c_int, W.WPARAM, W.LPARAM]
u32.CallNextHookEx.restype = ctypes.c_ssize_t
k32.GetModuleHandleW.argtypes = [W.LPCWSTR]
k32.GetModuleHandleW.restype = ctypes.c_void_p
u32.GetDC.argtypes = [ctypes.c_void_p]
u32.GetDC.restype = ctypes.c_void_p
u32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi.GetPixel.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
gdi.GetPixel.restype = W.DWORD


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", W.LONG), ("dy", W.LONG), ("data", W.DWORD), ("flags", W.DWORD), ("time", W.DWORD), ("extra", PTR)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("vk", W.WORD), ("scan", W.WORD), ("flags", W.DWORD), ("time", W.DWORD), ("extra", PTR)]


class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", W.DWORD), ("u", _U)]


class KBD(ctypes.Structure):  # KBDLLHOOKSTRUCT
    _fields_ = [("vk", W.DWORD), ("scan", W.DWORD), ("flags", W.DWORD), ("time", W.DWORD), ("extra", PTR)]


class MSL(ctypes.Structure):  # MSLLHOOKSTRUCT
    _fields_ = [("x", W.LONG), ("y", W.LONG), ("data", W.DWORD), ("flags", W.DWORD), ("time", W.DWORD), ("extra", PTR)]


EXT = {33, 34, 35, 36, 37, 38, 39, 40, 44, 45, 46, 91, 92, 93, 111, 144, 163, 165}  # 확장 키
MODS = {16, 17, 18, 160, 161, 162, 163, 164, 165}
BTN_FLAG = {"left": (2, 4), "right": (8, 0x10), "middle": (0x20, 0x40)}  # (누르기, 떼기)
MB = {0x201: ("left", "down"), 0x202: ("left", "up"), 0x204: ("right", "down"), 0x205: ("right", "up"),
      0x207: ("middle", "down"), 0x208: ("middle", "up")}


def _send(inp):
    return u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def send_key(vk, up):
    return _send(INPUT(1, _U(ki=KEYBDINPUT(vk, u32.MapVirtualKeyW(vk, 0), (2 if up else 0) | (1 if vk in EXT else 0), 0, 0))))


def send_mouse(flags, data=0):
    return _send(INPUT(0, _U(mi=MOUSEINPUT(0, 0, data & 0xFFFFFFFF, flags, 0, 0))))


def send_text(s, wait):
    for ch in s:
        if ch == "\n":
            send_key(13, False), send_key(13, True)
        else:
            b = ch.encode("utf-16-le")
            for i in range(0, len(b), 2):  # 유니코드로 직접 입력 → 한/영 상태와 무관
                u = int.from_bytes(b[i:i + 2], "little")
                _send(INPUT(1, _U(ki=KEYBDINPUT(0, u, 4, 0, 0))))
                _send(INPUT(1, _U(ki=KEYBDINPUT(0, u, 6, 0, 0))))
        if wait(0.005):
            return


def cursor():
    p = W.POINT()
    u32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def pixel(x, y):
    dc = u32.GetDC(None)
    c = gdi.GetPixel(dc, x, y)
    u32.ReleaseDC(None, dc)
    return None if c == 0xFFFFFFFF else (c & 255, (c >> 8) & 255, (c >> 16) & 255)


def mods():
    down = lambda vk: bool(u32.GetAsyncKeyState(vk) & 0x8000)
    return down(17) | down(18) << 1 | down(16) << 2


Q = queue.Queue()   # 훅·실행 스레드 → 화면 스레드
REC_ON = [False]    # 기록 중일 때만 마우스 이벤트를 받는다


def hooks():
    def kb(n, wp, lp):
        if n == 0:
            k = KBD.from_address(lp)
            if not k.flags & 0x10:  # 매크로가 보낸 입력은 무시
                Q.put(("key", k.vk, wp in (0x100, 0x104), time.perf_counter()))
        return u32.CallNextHookEx(None, n, wp, lp)

    def ms(n, wp, lp):
        if n == 0 and REC_ON[0]:
            m = MSL.from_address(lp)
            if not m.flags & 1:
                Q.put(("mouse", wp, m.x, m.y, ctypes.c_short(m.data >> 16).value, time.perf_counter()))
        return u32.CallNextHookEx(None, n, wp, lp)

    procs = HOOKPROC(kb), HOOKPROC(ms)
    mod = k32.GetModuleHandleW(None)
    if not (u32.SetWindowsHookExW(13, procs[0], mod, 0) and u32.SetWindowsHookExW(14, procs[1], mod, 0)):
        Q.put(("error",))
    msg = W.MSG()
    while u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
        pass


# ───────────────────────── 이름·설명 ─────────────────────────
VK = {8: "Backspace", 9: "Tab", 13: "Enter", 19: "Pause", 20: "CapsLock", 21: "한/영", 25: "한자", 27: "Esc",
      32: "Space", 33: "PageUp", 34: "PageDown", 35: "End", 36: "Home", 37: "←", 38: "↑", 39: "→", 40: "↓",
      44: "PrintScreen", 45: "Insert", 46: "Delete", 91: "Win", 92: "RWin", 93: "Menu", 106: "Num*", 107: "Num+",
      109: "Num-", 110: "Num.", 111: "Num/", 144: "NumLock", 145: "ScrollLock", 160: "Shift", 161: "RShift",
      162: "Ctrl", 163: "RCtrl", 164: "Alt", 165: "RAlt", 186: ";", 187: "=", 188: ",", 189: "-", 190: ".",
      191: "/", 192: "`", 219: "[", 220: "\\", 221: "]", 222: "'"}
VK.update({c: chr(c) for c in [*range(48, 58), *range(65, 91)]})
VK.update({96 + i: f"Num{i}" for i in range(10)})
VK.update({111 + i: f"F{i}" for i in range(1, 25)})

ACT = {"click": "누르고 떼기", "down": "누르기", "up": "떼기"}
MOVE = {"abs": "절대 위치로 이동", "rel": "현재 위치에서 이동", "none": "이동 안 함"}
BTN = {"left": "왼쪽", "right": "오른쪽", "middle": "가운데", "none": "없음"}


def key_name(vk):
    return VK.get(vk, f"키{vk}")


def hk_name(hk):
    if not hk:
        return "(없음)"
    return "".join(n + "+" for b, n in ((1, "Ctrl"), (2, "Alt"), (4, "Shift")) if hk[1] & b) + key_name(hk[0])


def describe(e):
    t = e["t"]
    if t == "key":
        return f"키보드  {key_name(e['vk'])}  {ACT[e['act']]}"
    if t == "mouse":
        s = "마우스"
        if e["move"] != "none":
            s += f"  {'절대' if e['move'] == 'abs' else '상대'}({e['x']}, {e['y']})"
        if e["btn"] != "none":
            s += f"  {BTN[e['btn']]} {ACT[e['act']]}"
        return s + (f"  휠 {e['wheel']}" if e["wheel"] else "")
    if t == "delay":
        return f"지연  {e['sec']:.3f}초" + (f"  ±{e['rand']}%" if e["rand"] else "")
    if t == "color":
        return f"색 대기  ({e['r']}, {e['g']}, {e['b']}) ±{e['tol']}  위치({e['x']}, {e['y']})"
    if t == "text":
        return "문자열  " + e["s"].replace("\n", "⏎")
    return f"매크로 호출  '{e['name']}'"


def build(raw, min_delay=None, merge=True):
    """기록한 (시각, 이벤트) 목록 → 매크로 이벤트. 누르기+떼기는 합치고, 긴 간격은 지연으로 넣는다."""
    out, end, i = [], None, 0
    while i < len(raw):
        t, e = raw[i]
        t_end = t
        if merge and e.get("act") == "down" and i + 1 < len(raw) and raw[i + 1][1] == {**e, "act": "up"}:
            e, t_end, i = {**e, "act": "click"}, raw[i + 1][0], i + 1
        if min_delay is not None and end is not None and t - end >= min_delay:
            out.append({"t": "delay", "sec": round(t - end, 3), "rand": 0})
        out.append(e)
        end, i = t_end, i + 1
    return out


# ───────────────────────── 실행 ─────────────────────────
class Runner(threading.Thread):
    def __init__(self, macro, find, key):
        super().__init__(daemon=True)
        self.macro, self.find, self.key = copy.deepcopy(macro), find, key
        self.stop, self.held = threading.Event(), set()

    def run(self):
        try:
            n = 0
            while not self.stop.is_set() and (self.macro["repeat"] == 0 or n < self.macro["repeat"]):
                self.play(self.macro, 0)
                n += 1
        finally:
            for what in list(self.held):  # 중지 시 눌린 키·버튼을 모두 뗀다
                self.emit(what, True)
            Q.put(("done", self.key, self))

    def play(self, m, depth):
        for e in m["events"]:
            if self.stop.is_set():
                return
            self.do(e, depth)
            self.stop.wait(GAP)

    def emit(self, what, up):
        kind, v = what
        send_key(v, up) if kind == "k" else send_mouse(BTN_FLAG[v][up])
        (self.held.discard if up else self.held.add)(what)

    def press(self, what, act):
        if act != "up":
            self.emit(what, False)
        if act == "click":
            self.stop.wait(0.02)
        if act != "down":
            self.emit(what, True)

    def do(self, e, depth):
        t = e["t"]
        if t == "key":
            self.press(("k", e["vk"]), e["act"])
        elif t == "mouse":
            if e["move"] != "none":
                x, y = (0, 0) if e["move"] == "abs" else cursor()
                u32.SetCursorPos(x + e["x"], y + e["y"])
                time.sleep(0.01)
            if e["btn"] != "none":
                self.press(("m", e["btn"]), e["act"])
            if e["wheel"]:
                send_mouse(0x800, e["wheel"])
        elif t == "delay":
            self.stop.wait(e["sec"] * (1 + random.uniform(-e["rand"], e["rand"]) / 100))
        elif t == "color":
            while not self.stop.is_set():
                p = pixel(e["x"], e["y"])
                if p and all(abs(a - b) <= e["tol"] for a, b in zip(p, (e["r"], e["g"], e["b"]))):
                    break
                self.stop.wait(0.05)
        elif t == "text":
            send_text(e["s"], self.stop.wait)
        elif t == "call":
            m = self.find(e["name"])
            if depth >= 20:  # 서로 부르는 매크로(순환 호출) 방지
                self.stop.set()
            elif m:
                for _ in range(max(1, m["repeat"])):
                    self.play(m, depth + 1)


# ───────────────────────── 저장 ─────────────────────────
DEFAULT_SET = {"toggle": [119, 0], "record": [120, 0], "capture": [118, 0]}  # F8, F9, F7


def load(path):
    data = {"settings": dict(DEFAULT_SET), "macros": []}
    if path.exists():
        try:
            d = json.loads(path.read_text(encoding="utf-8"))
            data["settings"].update(d["settings"])
            data["macros"] = d["macros"]
        except (ValueError, KeyError, TypeError):
            bad = path.with_suffix(".깨짐.json")
            path.replace(bad)  # 덮어써서 잃지 않도록 따로 둔다
            messagebox.showerror(TITLE, f"매크로 파일을 읽지 못했습니다.\n{bad.name} 로 옮겨 두고 빈 목록으로 시작합니다.")
    return data


def save(path, data):
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(path)


# ───────────────────────── 화면 ─────────────────────────
class Modal(tk.Toplevel):
    def __init__(self, parent, title):
        super().__init__(parent)
        self.title(title)
        self.transient(parent)
        self.resizable(False, False)
        self.attributes("-topmost", True)
        self.result = None

    def show(self):
        self.grab_set()
        self.wait_window()
        if isinstance(self.master, tk.Toplevel) and self.master.winfo_exists():
            self.master.grab_set()
        return self.result


class KeyPick(ttk.Frame):
    """버튼을 누른 뒤 키를 누르면 그 키가 지정된다. 값은 [가상키, 조합키] 또는 None."""

    def __init__(self, parent, app, value, allow_mod=False):
        super().__init__(parent)
        self.app, self.allow = app, allow_mod
        self.btn = ttk.Button(self, width=20, command=self.pick)
        self.btn.pack(side="left")
        if not allow_mod:
            ttk.Button(self, text="지우기", width=6, command=lambda: self.set(None)).pack(side="left")
        self.set(value)

    def set(self, v):
        self.value = v
        self.btn["text"] = hk_name(v)

    def pick(self):
        self.btn["text"] = "키를 누르세요…"
        self.winfo_toplevel().focus_set()  # Space·Enter 가 이 버튼을 다시 누르지 않게
        self.app.capture = (self.set, self.allow)


class Form(Modal):
    """(키, 이름표, 종류) 목록으로 만드는 입력 창. 종류: int/float, "choice", "key", "hotkey", "text"."""

    def __init__(self, parent, app, title, fields, init, check=None):
        super().__init__(parent, title)
        self.app, self.check, self.get, self.var = app, check, {}, {}
        for r, (k, label, kind, *x) in enumerate(fields):
            ttk.Label(self, text=label).grid(row=r, column=0, sticky="e", padx=6, pady=3)
            v = init.get(k)
            if kind == "choice":
                opts = x[0]
                var = tk.StringVar(value=opts.get(v, next(iter(opts.values()))))
                wd = ttk.Combobox(self, values=list(opts.values()), textvariable=var, state="readonly", width=20)
                self.get[k] = lambda var=var, opts=opts: next(a for a, b in opts.items() if b == var.get())
            elif kind == "key":
                wd = KeyPick(self, app, [v, 0] if v else None, True)
                self.get[k] = lambda wd=wd: wd.value[0]
            elif kind == "hotkey":
                wd = KeyPick(self, app, v)
                self.get[k] = lambda wd=wd: wd.value or [][0]
            elif kind == "text":
                wd = tk.Text(self, width=40, height=5)
                wd.insert("1.0", v or "")
                self.get[k] = lambda wd=wd: wd.get("1.0", "end-1c")
            else:
                var = self.var[k] = tk.StringVar(value="" if v is None else v)
                wd = ttk.Entry(self, textvariable=var, width=22)
                self.get[k] = lambda var=var, kind=kind: kind(var.get())
            wd.grid(row=r, column=1, sticky="w", padx=6, pady=3)
        r = len(fields)
        if "x" in self.var:
            what = "마우스 위치와 그 자리 색" if "r" in self.var else "마우스 위치"
            ttk.Label(self, text=f"{hk_name(app.data['settings']['capture'])} 키: 현재 {what} 가져오기",
                      foreground="#06c").grid(row=r, column=0, columnspan=2, pady=4)
            app.pos_cb = self.fill
        f = ttk.Frame(self)
        f.grid(row=r + 1, column=0, columnspan=2, pady=8)
        ttk.Button(f, text="확인", command=self.ok).pack(side="left", padx=4)
        ttk.Button(f, text="취소", command=self.destroy).pack(side="left", padx=4)

    def fill(self, x, y, rgb):
        for k, v in zip("xyrgb", (x, y, *rgb)):
            if k in self.var:
                self.var[k].set(v)

    def ok(self):
        try:
            res = {k: g() for k, g in self.get.items()}
        except (ValueError, TypeError, IndexError):
            return messagebox.showerror(TITLE, "입력 값을 확인하세요.", parent=self)
        err = self.check and self.check(res)
        if err:
            return messagebox.showerror(TITLE, err, parent=self)
        self.result = res
        self.destroy()

    def destroy(self):
        if self.app.pos_cb == self.fill:
            self.app.pos_cb = None
        super().destroy()


FIELDS = {
    "key": ("키보드", [("vk", "키 (버튼을 누르고 키 입력)", "key"), ("act", "동작", "choice", ACT)]),
    "mouse": ("마우스", [("move", "이동", "choice", MOVE), ("x", "X", int), ("y", "Y", int), ("btn", "버튼", "choice", BTN),
                      ("act", "동작", "choice", ACT), ("wheel", "휠 (위 120, 아래 -120, 안 쓰면 0)", int)]),
    "delay": ("지연", [("sec", "시간(초)", float), ("rand", "랜덤 ±%", int)]),
    "color": ("색 대기", [("x", "X", int), ("y", "Y", int), ("r", "R", int), ("g", "G", int), ("b", "B", int),
                       ("tol", "R·G·B 허용 편차", int)]),
    "text": ("문자열", [("s", "입력할 글", "text")]),
    "call": ("매크로 호출", []),
}
NEW = {"key": {"act": "click"}, "mouse": {"move": "abs", "x": 0, "y": 0, "btn": "left", "act": "click", "wheel": 0},
       "delay": {"sec": 1.0, "rand": 0}, "color": {"x": 0, "y": 0, "r": 0, "g": 0, "b": 0, "tol": 10},
       "text": {}, "call": {}}
CHECK = {
    "mouse": lambda r: "이동·버튼·휠 중 하나는 골라야 합니다." if r["move"] == "none" and r["btn"] == "none" and not r["wheel"] else None,
    "delay": lambda r: None if 0.001 <= r["sec"] <= 86400 and 0 <= r["rand"] <= 100 else "시간은 0.001~86400초, 랜덤은 0~100% 입니다.",
    "color": lambda r: None if all(0 <= r[k] <= 255 for k in ("r", "g", "b", "tol")) else "색과 편차는 0~255 입니다.",
    "text": lambda r: None if r["s"] else "글을 입력하세요.",
}


def ask_event(parent, app, e, others):
    title, fields = FIELDS[e["t"]]
    if e["t"] == "call":
        if not others:
            return messagebox.showinfo(TITLE, "호출할 다른 매크로가 없습니다.", parent=parent) and None
        fields = [("name", "호출할 매크로", "choice", {n: n for n in others})]
    r = Form(parent, app, title + " 이벤트", fields, e, CHECK.get(e["t"])).show()
    return r and {"t": e["t"], **r}


class MacroDialog(Modal):
    def __init__(self, app, macro, others):
        super().__init__(app.root, "매크로 편집")
        self.app, self.m, self.others = app, copy.deepcopy(macro), others
        top = ttk.Frame(self)
        top.grid(row=0, column=0, columnspan=2, sticky="w", padx=8, pady=6)
        ttk.Label(top, text="매크로 이름:").pack(side="left")
        self.name = tk.StringVar(value=self.m["name"])
        ttk.Entry(top, textvariable=self.name, width=30).pack(side="left", padx=4)
        self.lb = tk.Listbox(self, width=52, height=18, activestyle="none")
        self.lb.grid(row=1, column=0, padx=(8, 0), pady=2)
        self.lb.bind("<Double-Button-1>", lambda _: self.edit())
        side = ttk.Frame(self)
        side.grid(row=1, column=1, sticky="n", padx=8)
        for t, (label, _) in FIELDS.items():
            ttk.Button(side, text=label + " 추가", width=14, command=lambda t=t: self.add(t)).pack(pady=1)
        for label, cmd in (("편집", self.edit), ("삭제", self.delete), ("▲ 위로", lambda: self.move(-1)), ("▼ 아래로", lambda: self.move(1))):
            ttk.Button(side, text=label, width=14, command=cmd).pack(pady=(8 if label == "편집" else 1, 1))
        opt = ttk.Frame(self)
        opt.grid(row=2, column=0, columnspan=2, sticky="w", padx=8, pady=6)
        ttk.Label(opt, text="반복 횟수 (0이면 무한):").grid(row=0, column=0, sticky="e")
        self.repeat = tk.StringVar(value=self.m["repeat"])
        ttk.Entry(opt, textvariable=self.repeat, width=8).grid(row=0, column=1, sticky="w", padx=4)
        ttk.Label(opt, text="시작 단축키:").grid(row=1, column=0, sticky="e")
        self.start = KeyPick(opt, app, self.m["start"])
        self.start.grid(row=1, column=1, sticky="w", padx=4, pady=2)
        ttk.Label(opt, text="중지 단축키 (없으면 시작 키로 중지):").grid(row=2, column=0, sticky="e")
        self.stop = KeyPick(opt, app, self.m["stop"])
        self.stop.grid(row=2, column=1, sticky="w", padx=4, pady=2)
        f = ttk.Frame(self)
        f.grid(row=3, column=0, columnspan=2, pady=8)
        ttk.Button(f, text="시험 실행", command=self.test).pack(side="left", padx=4)
        ttk.Button(f, text="중단", command=lambda: app.stop_run("\0test")).pack(side="left", padx=(4, 30))
        ttk.Button(f, text="확인", command=self.ok).pack(side="left", padx=4)
        ttk.Button(f, text="취소", command=self.destroy).pack(side="left", padx=4)
        self.fill()

    def fill(self, sel=None):
        self.lb.delete(0, "end")
        for e in self.m["events"]:
            self.lb.insert("end", describe(e))
        if sel is not None and self.m["events"]:
            self.lb.selection_set(sel)
            self.lb.see(sel)

    def sel(self):
        s = self.lb.curselection()
        return s[0] if s else None

    def add(self, t):
        e = ask_event(self, self.app, {"t": t, **NEW[t]}, self.others)
        if e:
            i = len(self.m["events"]) if self.sel() is None else self.sel() + 1
            self.m["events"].insert(i, e)
            self.fill(i)

    def edit(self):
        i = self.sel()
        if i is not None:
            e = ask_event(self, self.app, self.m["events"][i], self.others)
            if e:
                self.m["events"][i] = e
                self.fill(i)

    def delete(self):
        i = self.sel()
        if i is not None:
            del self.m["events"][i]
            self.fill(min(i, len(self.m["events"]) - 1))

    def move(self, d):
        i, ev = self.sel(), self.m["events"]
        if i is not None and 0 <= i + d < len(ev):
            ev[i], ev[i + d] = ev[i + d], ev[i]
            self.fill(i + d)

    def collect(self):
        name = self.name.get().strip()
        if not name:
            return "매크로 이름을 입력하세요."
        if name in self.others:
            return "같은 이름의 매크로가 있습니다."
        if not self.repeat.get().isdigit():
            return "반복 횟수는 0 이상의 숫자입니다."
        self.m.update(name=name, repeat=int(self.repeat.get()), start=self.start.value, stop=self.stop.value)

    def test(self):
        err = self.collect()
        if err:
            return messagebox.showerror(TITLE, err, parent=self)
        self.app.start_run(self.m, "\0test")

    def ok(self):
        err = self.collect()
        if err:
            return messagebox.showerror(TITLE, err, parent=self)
        self.result = self.m
        self.destroy()

    def destroy(self):
        self.app.stop_run("\0test")
        super().destroy()


class RecordDialog(Modal):
    def __init__(self, app):
        super().__init__(app.root, "매크로 기록")
        self.app, self.on, self.raw = app, False, []
        self.key = hk_name(app.data["settings"]["record"])
        self.state = tk.StringVar(value=f"{self.key} 키를 누르면 기록을 시작합니다.")
        ttk.Label(self, textvariable=self.state, foreground="#c00").grid(row=0, column=0, columnspan=3, padx=10, pady=8)
        self.opt = {k: tk.BooleanVar(value=v) for k, v in
                    (("kb", True), ("btn", True), ("move", False), ("wheel", True), ("delay", True), ("merge", True))}
        self.px, self.ms = tk.StringVar(value="20"), tk.StringVar(value="50")
        rows = (("kb", "키보드 키", None, ""), ("btn", "마우스 버튼 (위치 포함)", None, ""),
                ("move", "마우스 움직임:", self.px, "픽셀 이상 움직일 때"), ("wheel", "마우스 휠", None, ""),
                ("delay", "시간 지연:", self.ms, "밀리초 이상일 때"), ("merge", "누르기+떼기를 하나로 합치기", None, ""))
        for r, (k, label, var, unit) in enumerate(rows, 1):
            ttk.Checkbutton(self, text=label, variable=self.opt[k]).grid(row=r, column=0, sticky="w", padx=10)
            if var:
                ttk.Entry(self, textvariable=var, width=6).grid(row=r, column=1)
                ttk.Label(self, text=unit).grid(row=r, column=2, sticky="w", padx=4)
        ttk.Button(self, text="취소", command=self.destroy).grid(row=9, column=0, columnspan=3, pady=8)

    def toggle(self):
        if self.on:
            self.on = REC_ON[0] = False
            self.result = build(self.raw, self.n_ms / 1000 if self.opt["delay"].get() else None, self.opt["merge"].get())
            return self.destroy()
        if not (self.px.get().isdigit() and self.ms.get().isdigit()):
            return self.state.set("픽셀·밀리초 숫자를 확인하세요.")
        self.n_px, self.n_ms, self.last = int(self.px.get()), int(self.ms.get()), cursor()
        self.on = REC_ON[0] = True
        self.add(None, None)

    def add(self, t, e):
        if e:
            self.raw.append((t, e))
        self.state.set(f"기록 중… {len(self.raw)}개  ({self.key} 키로 끝내기)")

    def mouse(self, msg, x, y, delta, t):
        e = {"t": "mouse", "move": "abs", "x": x, "y": y, "btn": "none", "act": "click", "wheel": 0}
        if msg == 0x200:
            if self.opt["move"].get() and max(abs(x - self.last[0]), abs(y - self.last[1])) >= self.n_px:
                self.last = (x, y)
                self.add(t, e)
        elif msg == 0x20A:
            if self.opt["wheel"].get():
                self.add(t, {**e, "move": "none", "x": 0, "y": 0, "wheel": delta})
        elif msg in MB and self.opt["btn"].get():
            self.add(t, {**e, "btn": MB[msg][0], "act": MB[msg][1]})

    def destroy(self):
        REC_ON[0] = False
        self.app.rec = None
        super().destroy()


class App:
    def __init__(self, path):
        self.root = root = tk.Tk()
        root.title(f"{TITLE} v{__version__}")
        ttk.Label(root, text=f"v{__version__}  ·  개발자 {AUTHOR}", foreground="#888").grid(row=3, column=0, columnspan=2, sticky="e", padx=8, pady=(0, 6))
        root.attributes("-topmost", True)
        root.report_callback_exception = lambda *a: messagebox.showerror(TITLE, "".join(traceback.format_exception(*a)))
        self.path, self.data = path, load(path)
        self.run_mode, self.runners, self.held = False, {}, set()
        self.capture = self.pos_cb = self.rec = None
        self.mode = tk.BooleanVar(value=False)
        top = ttk.Frame(root)
        top.grid(row=0, column=0, columnspan=2, sticky="w", padx=8, pady=6)
        ttk.Radiobutton(top, text="매크로 실행 가능", variable=self.mode, value=True, command=lambda: self.set_mode(True)).pack(side="left")
        ttk.Radiobutton(top, text="실행 중단과 편집", variable=self.mode, value=False, command=lambda: self.set_mode(False)).pack(side="left", padx=10)
        self.hint = ttk.Label(root, foreground="#06c")
        self.hint.grid(row=1, column=0, columnspan=2, sticky="w", padx=8)
        self.lb = tk.Listbox(root, width=44, height=14, activestyle="none")
        self.lb.grid(row=2, column=0, padx=(8, 0), pady=6)
        self.lb.bind("<Double-Button-1>", lambda _: self.edit())
        side = ttk.Frame(root)
        side.grid(row=2, column=1, sticky="n", padx=8, pady=6)
        for label, cmd in (("기록", self.record), ("추가", self.add), ("편집", self.edit), ("복사", self.copy),
                           ("삭제", self.delete), ("▲ 위로", lambda: self.move(-1)), ("▼ 아래로", lambda: self.move(1)),
                           ("실행 / 중지", self.run_selected), ("설정", self.settings)):
            ttk.Button(side, text=label, width=12, command=cmd).pack(pady=1)
        root.protocol("WM_DELETE_WINDOW", self.quit)
        self.refresh()
        root.after(10, self.pump)

    # ── 목록 ──
    @property
    def macros(self):
        return self.data["macros"]

    def find(self, name):
        return next((m for m in self.macros if m["name"] == name), None)

    def sel(self):
        s = self.lb.curselection()
        return s[0] if s else None

    def refresh(self, sel=None):
        sel = self.sel() if sel is None else sel
        self.lb.delete(0, "end")
        for m in self.macros:
            self.lb.insert("end", f"{'▶' if m['name'] in self.runners else '    '} {m['name']}    [{hk_name(m['start'])}]")
        if sel is not None and sel < len(self.macros):
            self.lb.selection_set(sel)
        self.hint["text"] = f"{hk_name(self.data['settings']['toggle'])} 키: 실행 가능 ↔ 중단 전환 (급할 때 모두 멈춤)"

    def changed(self, sel=None):
        save(self.path, self.data)
        self.refresh(sel)

    def open(self, m, index=None):
        """편집 창을 띄우고, 확인하면 목록에 넣거나(index 없음) 바꾼다."""
        self.set_mode(False)
        others = [x["name"] for i, x in enumerate(self.macros) if i != index]
        r = MacroDialog(self, m, others).show()
        if not r:
            return
        if index is None:
            self.macros.append(r)
            index = len(self.macros) - 1
        else:
            for x in self.macros:  # 이름이 바뀌면 이 매크로를 부르는 곳도 고친다
                for e in x["events"]:
                    if e["t"] == "call" and e["name"] == m["name"]:
                        e["name"] = r["name"]
            self.macros[index] = r
        self.changed(index)

    def new_name(self, base):
        n = 1
        while self.find(f"{base} {n}"):
            n += 1
        return f"{base} {n}"

    def add(self, events=()):
        self.open({"name": self.new_name("기록" if events else "매크로"), "repeat": 1, "start": None, "stop": None, "events": list(events)})

    def edit(self):
        if self.sel() is not None:
            self.open(self.macros[self.sel()], self.sel())

    def copy(self):
        if self.sel() is not None:
            m = copy.deepcopy(self.macros[self.sel()])
            m.update(name=self.new_name(m["name"] + " 복사"), start=None, stop=None)
            self.macros.append(m)
            self.changed(len(self.macros) - 1)

    def delete(self):
        i = self.sel()
        if i is not None and messagebox.askyesno(TITLE, f"'{self.macros[i]['name']}' 매크로를 삭제할까요?"):
            self.stop_run(self.macros[i]["name"])
            del self.macros[i]
            self.changed()

    def move(self, d):
        i, ms = self.sel(), self.macros
        if i is not None and 0 <= i + d < len(ms):
            ms[i], ms[i + d] = ms[i + d], ms[i]
            self.changed(i + d)

    def record(self):
        self.set_mode(False)
        self.rec = RecordDialog(self)
        events = self.rec.show()
        if events:
            self.add(events)

    def settings(self):
        self.set_mode(False)
        fields = [("toggle", "실행 가능 ↔ 중단 전환 키", "hotkey"), ("record", "기록 시작/중지 키", "hotkey"),
                  ("capture", "마우스 위치·색 가져오기 키", "hotkey")]
        r = Form(self.root, self, "설정", fields, self.data["settings"]).show()
        if r:
            self.data["settings"] = r
            self.changed()

    # ── 실행 ──
    def set_mode(self, on):
        self.run_mode = on
        self.mode.set(on)
        if not on:
            for r in self.runners.values():
                r.stop.set()

    def start_run(self, m, key=None):
        key = key or m["name"]
        if key not in self.runners:
            self.runners[key] = Runner(m, self.find, key)
            self.runners[key].start()
            self.refresh()

    def stop_run(self, key):
        if key in self.runners:
            self.runners[key].stop.set()

    def run_selected(self):
        if self.sel() is not None:
            m = self.macros[self.sel()]
            self.stop_run(m["name"]) if m["name"] in self.runners else self.start_run(m)

    # ── 훅에서 온 입력 ──
    def pump(self):
        try:
            while True:
                m = Q.get_nowait()
                if m[0] == "key":
                    self.on_key(*m[1:])
                elif m[0] == "mouse":
                    if self.rec and self.rec.on:
                        self.rec.mouse(*m[1:])
                elif m[0] == "done":
                    if self.runners.get(m[1]) is m[2]:
                        del self.runners[m[1]]
                    self.refresh()
                elif m[0] == "update":
                    ver, exe_url, sha_url, notes = m[1]
                    if messagebox.askyesno(TITLE, f"새 버전 v{ver} 이 있습니다. 지금 업데이트할까요?\n\n{notes[:600]}"):
                        self.set_mode(False)
                        updater.install(exe_url, sha_url)
                        self.root.destroy()
                else:
                    messagebox.showerror(TITLE, "키보드·마우스 감시를 시작하지 못했습니다. 단축키와 기록이 동작하지 않습니다.")
        except queue.Empty:
            pass
        finally:
            self.root.after(10, self.pump)

    def on_key(self, vk, down, t):
        if not down:
            self.held.discard(vk)
        elif vk in self.held:
            return  # 누르고 있을 때 반복되는 입력
        else:
            self.held.add(vk)
        hk, s = [vk, mods()], self.data["settings"]
        if self.capture:
            cb, allow = self.capture
            if down and (allow or vk not in MODS):
                self.capture = None
                try:
                    cb([vk, 0 if allow else hk[1]])
                except tk.TclError:  # 창이 이미 닫힘
                    pass
            return
        if hk == s["toggle"]:
            if down:  # 실행 중인 것이 있으면 무조건 멈춤. 편집 창이 떠 있으면 켜지 않는다
                self.set_mode(not (self.run_mode or self.runners) and not self.root.grab_current())
            return
        if self.rec:
            if hk == s["record"]:
                if down:
                    self.rec.toggle()
            elif self.rec.on and self.rec.opt["kb"].get():
                self.rec.add(t, {"t": "key", "vk": vk, "act": "down" if down else "up"})
            return
        if not down:
            return
        if self.pos_cb and hk == s["capture"]:
            x, y = cursor()
            return self.pos_cb(x, y, pixel(x, y) or (0, 0, 0))
        if self.run_mode:
            for m in self.macros:
                running = m["name"] in self.runners
                if hk == m["start"] and not running:
                    self.start_run(m)
                elif running and hk == (m["stop"] or m["start"]):
                    self.stop_run(m["name"])

    def quit(self):
        self.set_mode(False)
        self.root.destroy()


def selftest():
    k = lambda act, vk=65: {"t": "key", "vk": vk, "act": act}
    raw = [(0.0, k("down")), (0.05, k("up")), (1.0, k("down", 160)), (1.2, k("down", 66)), (1.25, k("up", 66)), (1.3, k("up", 160))]
    ev = build(raw, 0.5)
    assert ev == [k("click"), {"t": "delay", "sec": 0.95, "rand": 0}, k("down", 160), k("click", 66), k("up", 160)], ev
    assert build(raw, None, False) == [e for _, e in raw]
    assert hk_name([116, 3]) == "Ctrl+Alt+F5" and hk_name(None) == "(없음)"
    samples = [k("click"), {"t": "mouse", **NEW["mouse"]}, {"t": "delay", **NEW["delay"]}, {"t": "color", **NEW["color"]},
               {"t": "text", "s": "가\nb"}, {"t": "call", "name": "x"}]
    assert all(describe(e) for e in samples)
    assert CHECK["delay"]({"sec": 0, "rand": 0}) and not CHECK["delay"]({"sec": 1, "rand": 30})
    assert ctypes.sizeof(INPUT) == 40 and send_mouse(1) == 1  # 0칸 이동: 입력 보내기가 되는지만 확인
    assert pixel(*cursor()) is not None
    threading.Thread(target=hooks, daemon=True).start()
    time.sleep(0.3)
    assert Q.empty(), "훅 설치 실패"
    # 실행기: 지연·호출·반복·중지
    sub = {"name": "s", "repeat": 2, "start": None, "stop": None, "events": [{"t": "delay", "sec": 0.01, "rand": 50}]}
    main = {"name": "m", "repeat": 2, "start": None, "stop": None, "events": [{"t": "call", "name": "s"}, {"t": "call", "name": "없음"}]}
    r = Runner(main, {"s": sub}.get, "m")
    r.start(), r.join(5)
    assert not r.is_alive() and Q.get_nowait()[0] == "done"
    loop = {"name": "l", "repeat": 0, "start": None, "stop": None, "events": [{"t": "call", "name": "l"}]}
    r = Runner(loop, {"l": loop}.get, "l")  # 자기 자신을 부르는 매크로는 스스로 멈춘다
    r.start(), r.join(5)
    assert not r.is_alive()
    # 저장·화면 만들기
    import tempfile
    p = Path(tempfile.mkdtemp()) / "t.json"
    save(p, {"settings": DEFAULT_SET, "macros": [{**main, "events": samples}]})
    app = App(p)
    assert app.macros[0]["events"] == samples
    for d in (MacroDialog(app, app.macros[0], []), RecordDialog(app), Form(app.root, app, "t", FIELDS["mouse"][1], NEW["mouse"]),
              Form(app.root, app, "t", FIELDS["color"][1], NEW["color"])):
        app.root.update()
        d.destroy()
    app.on_key(119, True, 0)  # F8 → 실행 가능
    assert app.run_mode
    app.on_key(119, False, 0), app.on_key(119, True, 0)
    assert not app.run_mode
    app.root.destroy()
    print("selftest ok")


if __name__ == "__main__":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # 화면 배율과 상관없이 실제 픽셀 좌표를 쓴다
    except OSError:
        pass
    if "--selftest" in sys.argv:
        selftest()
    else:
        def check_update():
            try:
                found = updater.check()
            except Exception:  # 인터넷이 없거나 릴리스가 없으면 조용히 넘어간다
                return
            if found:
                Q.put(("update", found))

        if getattr(sys, "frozen", False):  # exe 로 실행할 때만 자동 업데이트
            updater.cleanup()
            threading.Thread(target=check_update, daemon=True).start()
        threading.Thread(target=hooks, daemon=True).start()
        App(Path(sys.argv[0]).resolve().parent / "매크로.json").root.mainloop()
