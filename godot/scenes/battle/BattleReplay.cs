using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D2.Data.Battle;
using D2.Data.Corpus;
using D2.Data.Dbf;
using D2.Data.Gfx;
using D2.Rules.Battle;
using Godot;

namespace D2.Godot;

/// <summary>Проигрывание записанного боя из корпуса: слева нападающие, справа обороняющиеся,
/// под каждым — HP. Ходы берутся из журнала (ReplayTurns), не из модели: задача сцены — показать
/// бой как он записан. Ввода нет — это Ф4.
/// Без окна (--headless) печатает <c>turns: N</c> и выходит; с <c>--show</c> проигрывает ходы по таймеру.</summary>
public partial class BattleReplay : Node2D
{
    private const double TurnSeconds = 0.8;
    private static readonly Vector2 FrameCenter = new(400, 300); // кадры BatUnits — холст 800x600

    private readonly Dictionary<string, Label> _hp = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ReplayTurn> _turns = [];
    private int _step;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        string journal = ResolvePath(ArgValue(args, "--journal") ?? "../corpus/sample/one_battle.jsonl");
        if (!File.Exists(journal)) { Fail($"журнал не найден ({journal})"); return; }

        // Правила — из данных порта (data/rules); копия игры нужна только за картинками юнитов (BaseUnits).
        string rules = GameData.RulesDir;
        if (!Directory.Exists(rules)) { Fail($"нет таблиц порта ({rules}); запустите tools/RulesImport"); return; }

        BattleState state;
        try
        {
            var battle = CorpusReader.Battles(journal).FirstOrDefault(b => b.End is not null);
            if (battle is null) { Fail("в журнале нет законченного боя"); return; }
            var gameRules = RulesLoader.Load(rules);
            if (!CorpusBattleLoader.TryLoad(battle, gameRules, out state, out string reason)) { Fail(reason); return; }
            _turns = ReplayTurns.Of(battle);
            BuildScene(state, BaseUnits(rules));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            // IOException покрывает InvalidDataException (дубль id в ReplayTurns) и битые .dbf/.jsonl;
            // JsonException — схема журнала разошлась с CorpusReader.
            Fail($"{e.GetType().Name}: {e.Message}");
            return;
        }
        GD.Print($"turns: {_turns.Count}");

        if (!args.Contains("--show")) { GetTree().Quit(); return; }
        var timer = new Timer { WaitTime = TurnSeconds, Autostart = true };
        timer.Timeout += Advance;
        AddChild(timer);
    }

    private void Fail(string message)
    {
        GD.PrintErr($"BattleReplay: {message}");
        GetTree().Quit(1);
    }

    private void BuildScene(BattleState state, IReadOnlyDictionary<string, string> baseUnits)
    {
        foreach (var unit in state.Units)
        {
            // ponytail: расстановка по слотам условная (ряд по slot, сторона по Attacker);
            // настоящие координаты слотов движка — отдельная таблица, понадобится при группах 2+.
            var position = new Vector2(unit.Attacker ? 330 : 820, 100 + unit.Slot * 90);
            var (frames, used) = LoadIdle(unit.Type.GlobalId, unit.Attacker, baseUnits);
            if (frames is not null)
                AddChild(new AnimatedSprite2D { Position = position, SpriteFrames = frames, Autoplay = "idle" });
            else
                AddChild(new ColorRect { Position = position - new Vector2(40, 60), Size = new Vector2(80, 120),
                                        Color = unit.Attacker ? Colors.SteelBlue : Colors.IndianRed });

            var label = new Label { Text = $"{unit.Hp}/{unit.HpMax}" };
            label.Position = position + new Vector2(used.Position.X + used.Size.X / 2f, used.End.Y + 4) - FrameCenter
                             - new Vector2(24, 0);
            AddChild(label);
            _hp[unit.Id] = label;
            AddChild(new Label { Text = unit.Type.Id, Position = label.Position + new Vector2(-30, 20) });
        }
    }

    private void Advance()
    {
        if (_step >= _turns.Count) { GetTree().Quit(); return; }
        var turn = _turns[_step++];
        foreach (var (id, hp) in turn.HpAfter)
            if (_hp.TryGetValue(id, out var label)) label.Text = $"{hp}/{label.Text.Split('/')[1]}";
        GD.Print(turn.Miss ? $"{_step}: {turn.Unit} miss" : $"{_step}: {turn.Unit} -> {turn.Target} dmg {turn.Damage}");
    }

    /// <summary>Кадры простоя анимации BatUnits <c>&lt;TYPE&gt;IDLEA1{A|D}00</c> по описи (A — сторона нападающего, D — обороняющегося;
    /// файлы — только занятая часть холста, Г19: холст восстанавливает <see cref="GfxTextures.Canvas"/>). У порождённых типов (BASE_UNIT
    /// в Gunits) анимация лежит под базовым типом. Возвращает кадры и занятый прямоугольник первого кадра (для подписи HP).</summary>
    private static (SpriteFrames? Frames, Rect2I Used) LoadIdle(string unitType, bool attacker,
                                                               IReadOnlyDictionary<string, string> baseUnits)
    {
        var gfx = GameData.Content.Gfx;
        string side = attacker ? "A" : "D";
        var anim = gfx.Anim("BatUnits", $"{unitType.ToUpperInvariant()}IDLEA1{side}00");
        if (anim is null && baseUnits.TryGetValue(unitType, out string? baseUnit))
            anim = gfx.Anim("BatUnits", $"{baseUnit.ToUpperInvariant()}IDLEA1{side}00");
        var files = anim is null ? [] : GfxAssets.FramePaths(anim, GameData.GfxRoot).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            GD.PrintErr($"BattleReplay: нет кадров простоя {unitType} (python tools/gfx-assets/build_gfx_assets.py BatUnits)");
            return (null, new Rect2I(360, 260, 80, 260));
        }

        var frames = new SpriteFrames();
        frames.AddAnimation("idle");
        frames.SetAnimationLoopMode("idle", SpriteFrames.LoopMode.Linear);
        frames.SetAnimationSpeed("idle", 10);
        Rect2I used = default;
        foreach (string file in files)
        {
            Image? image;
            try { image = GfxTextures.Canvas(file); } // битый файл: Godot возвращает null или бросает
            catch (Exception e) { GD.PrintErr($"BattleReplay: кадр {file} не читается ({e.Message})"); continue; }
            if (image is null) { GD.PrintErr($"BattleReplay: кадр {file} не читается"); continue; }
            if (used == default) used = image.GetUsedRect();
            frames.AddFrame("idle", ImageTexture.CreateFromImage(image));
        }
        return frames.GetFrameCount("idle") > 0 ? (frames, used) : (null, new Rect2I(360, 260, 80, 260));
    }

    /// <summary>UNIT_ID -> BASE_UNIT из Gunits.dbf (только строки, где базовый тип задан).</summary>
    private static Dictionary<string, string> BaseUnits(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!GameTables.Has(dir, "Gunits.dbf")) return map;
        foreach (var row in GameTables.Read(dir, "Gunits.dbf").Rows)
        {
            string baseUnit = row.GetString("BASE_UNIT");
            if (!baseUnit.Equals("G000000000", StringComparison.OrdinalIgnoreCase)) map[row.GetString("UNIT_ID")] = baseUnit;
        }
        return map;
    }

    /// <summary>Как GamePaths.GameDir, но от res:// — в Godot AppContext.BaseDirectory указывает не в
    /// дерево репозитория.
    /// ponytail: дубль логики GamePaths; потолок — два места, где меняется путь к игре. Убрать, когда
    /// GamePaths примет базовый каталог параметром (задать из сцены), а не только из AppContext.</summary>
    private static string GameDir() =>
        System.Environment.GetEnvironmentVariable("D2_GAME_DIR")
        ?? GameData.FromRoot(Path.Combine("..", "disciples-modding", "game-pristine"));

    /// <summary>Относительный путь — от каталога проекта (godot/), как в брифе: <c>--journal ../corpus/...</c>.</summary>
    private static string ResolvePath(string path) => GameData.FromProject(path);

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
