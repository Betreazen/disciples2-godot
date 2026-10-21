# Инструменты реверса

Установлено 11.10.2026. Нужны, когда поведение не закрывается журналом зонда и надо читать код `Discipl2.exe`
(x86 PE, `../disciples-modding/game-pristine/`). Прежний путь — capstone в `../disciples-modding/tools/re` — остаётся.

| Что | Где | Зачем |
| --- | --- | --- |
| Ghidra 12.1.4 | `C:\Tools\ghidra\ghidra_12.1.4_PUBLIC` (12.1.3 рядом — откат) | движок: псевдокод, ссылки, вызовы |
| Шимы | `C:\Tools\bin\ghidra.cmd` (GUI), `ghidra-headless.cmd` | подставляют JDK 21 (системный `JAVA_HOME` — 17, Ghidra на нём не идёт) |
| REA 6.4.0 (`rea-agents`) | глобально через npm; MCP `rea` в `.mcp.json` | MCP поверх Ghidra: агент спрашивает функцию/адрес и получает псевдокод, асм, ссылки с Evidence ID |
| Скилл `reverse-engineer-anything` | `.claude/skills/` (из тега `rea-agents-6.4.0`, версия скилла 37) | порядок работы с REA |
| Образец: реконструкция DX-Ball | `../dx-ball-ref` (только читать) | методика: REA → типизированный код → сверка с оригиналом |

## Как пользоваться

```bash
rea doctor --provider ghidra --json        # готовность (Ghidra, JDK, нативные проверки Windows)
rea function "C:/Claude code projects/disciples-modding/game-pristine/Discipl2.exe" 0x00401000 \
  --provider ghidra --snapshot .analysis/rea/d2.snapshot.json --json
```

Через MCP — инструменты `open_binary`, `analyze_function`, `procedure_pseudo_code`, `procedure_assembly`, `read_bytes`.
Первый запрос холодный: `rea inspect` по `Discipl2.exe` (sha256 `88dc8162…`) — 5,5 мин, 42 411 процедур,
7068 строк (проверено 11.10.2026). Таймаут старта в `.mcp.json` — 10 мин. Для CLI переменные окружения
(`REA_ANALYSIS_PROVIDER`, `GHIDRA_INSTALL_DIR`, `JAVA_HOME` на JDK 21) задавать как в `.mcp.json`.

## Что взять из DX-Ball (`../dx-ball-ref/docs/agent/REA.md`, `WORKFLOW.md`)

- Ответы REA сохранять целиком с Evidence ID в `.analysis/rea/` (не в git); вывод в документе ссылается на запись.
- Псевдокод — гипотеза; утверждение доказывается сверкой: у них — побайтовой компиляцией, у нас — харнессом по
  журналу зонда (`docs/principles.md`). REA ускоряет поиск формулы, но не заменяет харнесс.
- Один открытый сеанс на серию вопросов к одному exe; спрашивать только нужные срезы (псевдокод/асм/байты).
- Снимок (`--snapshot`) привязан к версии REA: после обновления REA начинать новый, старые записи остаются доказательством.

## Ограничения

- Windows-поддержка Ghidra в REA экспериментальная (только чтение, 25 операций). Цель — на локальном NTFS, без
  ссылок/SUBST: копию игры не подсовывать через junction (это и наше правило безопасности).
- REA не ставит и не обновляет Ghidra/JDK — это делается вручную, шимы и `.mcp.json` править вместе.
