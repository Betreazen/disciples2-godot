<#
    Сборка зонда: считает ревизию сама и передаёт её MSBuild как ProbeGitHash,
    чтобы строка `start` журнала (`{"ev":"start",...,"probe":"<хеш>"}`) указывала
    на код, из которого собрана DLL.

    Хеш = `git rev-parse --short HEAD` + суффикс `+dirty`, если в `tools/d2probe`
    есть незакоммиченные правки. Поэтому собирать нужно ПОСЛЕ коммита исходников:
    хеш «до коммита» указал бы на предыдущее состояние кода.

    Сабмодули D2RSG/SLikeNet намеренно держат апстримные гитлинки, а правки под
    v143 лежат в рабочем дереве (apply-patches.sh, NOTICE.md) — их вечная
    «грязь» не признак незакоммиченного кода, отсюда --ignore-submodules=dirty.

    Порядок: шаги 0 и 1 README (apply-patches.sh, buildDetours.bat) — один раз
    руками, дальше этот скрипт.

        pwsh -File tools/d2probe/build.ps1
#>
[CmdletBinding()]
param(
    [string]$MSBuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path

if (-not (Test-Path $MSBuild)) {
    throw "MSBuild не найден: $MSBuild (другая редакция VS — передать -MSBuild <путь>)"
}

$hash = (& git -C $repo rev-parse --short HEAD)
if ($LASTEXITCODE -ne 0) { throw "git rev-parse не отработал в $repo" }
$hash = $hash.Trim()

$dirty = & git -C $repo status --porcelain --ignore-submodules=dirty -- tools/d2probe
if ($LASTEXITCODE -ne 0) { throw "git status не отработал в $repo" }
if ($dirty) {
    $hash = "$hash+dirty"
    Write-Warning "tools/d2probe с незакоммиченными правками — хеш помечен +dirty:`n$($dirty -join "`n")"
}

"ProbeGitHash=$hash"

& $MSBuild (Join-Path $here 'mss32\mss32.sln') `
    -p:Configuration=$Configuration `
    -p:Platform=x86 `
    '-p:DISCIPLES2_PATH=Release\' `
    "-p:ProbeGitHash=$hash" `
    -m
if ($LASTEXITCODE -ne 0) { throw "MSBuild вернул $LASTEXITCODE" }

# Проверка, а не обещание: хеш должен лежать в собранной DLL байтами.
$dll = Join-Path $here "mss32\$Configuration\mss32.dll"
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll))
if ($text -notmatch [regex]::Escape($hash)) {
    throw "в $dll нет строки '$hash' — ProbeGitHash не доехал до сборки"
}
"OK: $dll содержит '$hash'"
