# 릴리스 게시: version.py 버전 올리기 -> CHANGELOG 정리 -> 커밋·push -> 이 스크립트 실행
# GitHub 릴리스에 Key_Macro.exe + .sha256을 올린다. 자동 업데이트가 이 두 파일을 받는다.
$ErrorActionPreference = "Stop"
& .\build.ps1
$ver = (python -c "import version; print(version.__version__)").Trim()
$hash = (Get-FileHash dist\Key_Macro.exe -Algorithm SHA256).Hash.ToLower()
Set-Content -Encoding ascii dist\Key_Macro.exe.sha256 "$hash  Key_Macro.exe"
# CHANGELOG의 해당 버전 섹션을 릴리스 설명으로 사용
$notes = (Get-Content CHANGELOG.md -Raw -Encoding utf8) -split "(?m)^## " | Where-Object { $_.StartsWith("[$ver]") } | Select-Object -First 1
if (-not $notes) { throw "CHANGELOG.md에 [$ver] 섹션이 없습니다." }
$notes = ($notes -split "`n", 2)[1].Trim() -replace "(?s)\s*---\s*$", ""
$nf = New-TemporaryFile; [IO.File]::WriteAllText($nf, $notes)  # BOM 없이
git tag -f "v$ver"; git push -f origin "v$ver"
gh release create "v$ver" dist\Key_Macro.exe dist\Key_Macro.exe.sha256 --title "키보드·마우스 매크로 v$ver" --notes-file $nf
Remove-Item $nf
