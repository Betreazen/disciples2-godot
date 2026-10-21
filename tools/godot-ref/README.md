# Локальный справочник по API Godot

`godot47.jsonl` — весь публичный API установленного движка (Godot 4.7.2 mono):
1099 записей — 1037 классов, 38 встроенных типов (`Vector2`, `Color`, …), глобальные
перечисления (`Error`, `Key`, …) и глобальные функции. У каждого класса: предок,
свойства с типами, сигнатуры методов, сигналы, перечисления и константы,
описания обрезаны до первых предложений (BBCode снят).

Смысл: ответ на «как называется метод / что он принимает» стоит пары сотен токенов
и не требует ни интернета, ни угадывания.

## Запросы

```bash
python tools/godot-ref/godot_ref.py Sprite2D            # сводка по классу
python tools/godot-ref/godot_ref.py Sprite2D.texture    # одно свойство/метод/сигнал/константа
python tools/godot-ref/godot_ref.py Image.FORMAT_RGBA8  # константа: имя в C# и значение
python tools/godot-ref/godot_ref.py --search "viewport texture"
python tools/godot-ref/godot_ref.py --methods Node --grep child
python tools/godot-ref/godot_ref.py --props Image --grep format
python tools/godot-ref/godot_ref.py --tree Sprite2D     # цепочка наследования
```

Сводка класса печатает только **собственные** члены: унаследованное смотреть у предка
из первой строки (`Sprite2D : Node2D < CanvasItem < Node < Object`). Аксессоры
(`set_texture`/`get_texture`) из списка методов убраны — они видны как свойство.

## Имена: C# против GDScript

Документация Godot и сам дамп — в именах GDScript (`snake_case`). Справочник печатает
имя в виде C# и рядом в скобках оригинал: `Texture (texture)`, `AddChild (add_child)`.
Правило биндингов (`Godot.NET.Sdk`):

| Что | GDScript | C# |
|---|---|---|
| Класс | `Sprite2D` | `Sprite2D` (без изменений) |
| Метод | `add_child()` | `AddChild()` |
| Свойство | `sprite.texture` | `sprite.Texture` — **свойство**, не поле; есть и `GetTexture()/SetTexture()` |
| Сигнал | `frame_changed` | событие `FrameChanged`, испускание `EmitSignal(SignalName.FrameChanged)` |
| Перечисление | `Image.FORMAT_RGBA8` | `Image.Format.Rgba8` — общий префикс значений снимается |
| Константа вне перечисления | `Image.MAX_WIDTH` | `Image.MaxWidth` |
| Строковые имена | `"idle"` | `StringName`/`NodePath` строятся из строки неявно |

Конверсия имён в CLI механическая (каждый кусок `snake_case` → `Ucfirst`, остальное
в нижний регистр), у Godot есть единичные исключения — если C# не собирается,
проверять по оригинальному имени в скобках.

## Пересборка после обновления движка

Дамп с описаниями даёт только `--dump-extension-api-with-docs`; у `--doctool` на
релизной сборке все `<description>` пустые (проверено на 4.7.2).

```bash
python tools/godot-ref/build_godot_ref.py            # сам зовёт godot из C:\Tools\bin\godot.cmd
python tools/godot-ref/build_godot_ref.py --api путь/к/extension_api.json
GODOT_BIN=/путь/к/godot python tools/godot-ref/build_godot_ref.py
dotnet test tests/D2.GodotRef.Tests/D2.GodotRef.Tests.csproj
```

Файл переименовывать под новую версию не нужно: версия движка лежит в записи `@meta`
первой строкой, тест `tests/D2.GodotRef.Tests` проверяет её и несколько известных
ответов — так обновление Godot, сломавшее справочник, заметно сразу.

Размеры: полный `extension_api.json` — 11.8 МБ, справочник — 4.2 МБ (в git хранится
он; XML `--doctool` — 6.6 МБ в 1077 файлах и без описаний, потому не хранится).

Практические заметки по самому движку (headless, снимки экрана, ключ прозрачности,
коды возврата) — в `docs/godot-notes.md`.
