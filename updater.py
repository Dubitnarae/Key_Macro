"""자동 업데이트 (exe 전용).

GitHub 최신 릴리스에서 Key_Macro.exe와 Key_Macro.exe.sha256을 받아 SHA256이 맞을 때만 교체한다.
토큰 없이 읽으므로 저장소가 공개여야 한다. 실행 중인 exe는 이름 바꾸기는 되므로
현재 exe → .old, 새 exe → 원래 이름으로 바꾼 뒤 다시 실행한다. .old는 다음 실행 때 지운다.
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

from version import GITHUB_REPO, __version__

EXE_NAME = "Key_Macro.exe"
UA = {"User-Agent": f"Key_Macro/{__version__}"}


def is_newer(candidate: str, current: str = __version__) -> bool:
    t = lambda v: tuple(int(n) for n in re.findall(r"\d+", v))
    a, b = t(candidate), t(current)
    n = max(len(a), len(b))
    return a + (0,) * (n - len(a)) > b + (0,) * (n - len(b))


def _get(url: str, timeout: float = 30) -> bytes:
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=timeout) as r:
        return r.read()


def check():
    """새 버전이 있으면 (버전, exe 주소, sha256 주소, 릴리스 설명), 없으면 None. 실패는 예외."""
    rel = json.loads(_get(f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest", 8))
    ver = rel.get("tag_name", "").lstrip("vV")
    assets = {a["name"]: a["browser_download_url"] for a in rel.get("assets", [])}
    if not is_newer(ver) or EXE_NAME not in assets or EXE_NAME + ".sha256" not in assets:
        return None
    return ver, assets[EXE_NAME], assets[EXE_NAME + ".sha256"], rel.get("body") or ""


def verified(data: bytes, sha_text: str) -> bool:
    expected = sha_text.split()[0].strip().lower() if sha_text.strip() else ""
    return bool(re.fullmatch(r"[0-9a-f]{64}", expected)) and hashlib.sha256(data).hexdigest() == expected


def install(exe_url: str, sha_url: str) -> None:
    """내려받아 검증 후 교체하고 새 exe를 실행한다. 호출한 쪽은 곧바로 프로그램을 끝내야 한다."""
    cur = Path(sys.executable).resolve()
    data = _get(exe_url, 120)
    if not verified(data, _get(sha_url).decode("ascii", "replace")):
        raise RuntimeError("내려받은 파일의 SHA256이 맞지 않아 설치하지 않았습니다.")
    new, old = cur.with_suffix(".new"), cur.with_suffix(".old")
    new.write_bytes(data)
    old.unlink(missing_ok=True)
    cur.rename(old)
    try:
        new.rename(cur)
    except OSError:
        old.rename(cur)  # 되돌리기
        raise
    # 새 exe가 지금 exe의 임시 폴더(_MEI…) 설정을 물려받지 않게 한다. 안 하면 지금 exe가 끝날 때
    # 그 폴더를 못 지워 "Failed to remove temporary directory" 경고가 뜬다 (PyInstaller 6.9+)
    env = dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT="1")
    subprocess.Popen([str(cur)], cwd=str(cur.parent), close_fds=True, env=env)


def cleanup() -> None:
    if getattr(sys, "frozen", False):
        try:
            Path(sys.executable).resolve().with_suffix(".old").unlink(missing_ok=True)
        except OSError:
            pass  # 옛 프로그램이 아직 끝나는 중이면 다음 실행 때 지운다


if __name__ == "__main__":  # 자체 점검: python updater.py
    assert is_newer("0.3.0", "0.2.0") and is_newer("v0.2.1", "0.2.0") and is_newer("1.0", "0.9.9")
    assert not is_newer("0.2.0", "0.2.0") and not is_newer("0.1.9", "0.2.0")
    h = hashlib.sha256(b"abc").hexdigest()
    assert verified(b"abc", f"{h}  {EXE_NAME}\n") and not verified(b"abd", h) and not verified(b"abc", "")
    print("ok")
