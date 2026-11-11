# exe 빌드: 먼저 version.py 수정 -> 이 스크립트 실행
python key_macro.py --selftest
if ($LASTEXITCODE) { throw "자체 점검 실패" }
pyinstaller --noconfirm --onefile --windowed --name Key_Macro key_macro.py
