using D2.Data.Content;
using D2.Data.Map;
using D2.Data.Play;
using D2.Rules.Magic;
using D2.Rules.Map;

namespace D2.Data.Tests;

/// <summary>Эффекты заклинаний карты кроме удара, лечения и призыва (<c>GameSession.SpellEffects.cs</c>, <c>docs/ai-spells.md</c> §3)
/// и срок модификаторов карты (<see cref="MapState.SpellEffects"/>, <c>CMidSpellEffects</c>).</summary>
[Trait("Category", "Corpus")]
public class MapSpellEffectsTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const string Human = "S073PL000a";

    private static GameSession Load(string save)
    {
        string sg = Path.Combine(GameSaveTests.CorpusDir(), "20260919-human", save);
        Skip.IfNot(File.Exists(sg), "нет сохранения");
        var looks = MapLooksLoader.Load(sg);
        return new GameSession(new GameState(new GameInfo("", "", [Human], 1, 1), looks), SharedContent.Load(Data).MapArt,
                               GameBook.Load(Path.Combine(Data, "rules")));
    }

    /// <summary>
    /// Журнал human, день 62: человек бросает «G000SS0040» (усиление на отряд, seq 15480841) в (82,83) и «G000SS0088» (усиление
    /// своих по всей карте, seq 15480877). Порт повторяет оба литья поверх сохранения 102 (до них) — записи
    /// <c>CMidSpellEffects</c> этих заклинаний те же, что в сохранении 103: юнит, модификатор, заклинатель, срок.
    /// </summary>
    [SkippableFact]
    public void Boost_MatchesSaveRecords()
    {
        var before = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var after = Load("rastlennaya-vlast-dwarf-t62-103.sg");
        string[] spells = ["G000SS0040", "G000SS0088"];
        Assert.DoesNotContain(before.Map.SpellEffects, r => spells.Contains(r.OriginId, StringComparer.OrdinalIgnoreCase));
        var want = after.Map.SpellEffects.Where(r => spells.Contains(r.OriginId, StringComparer.OrdinalIgnoreCase))
                        .Select(Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(want.Count >= 7, $"записей в сохранении {want.Count}");

        Assert.Null(before.CastSpell(Human, "G000SS0040", 82, 83, useSpellBank: false));
        Assert.Null(before.CastSpell(Human, "G000SS0088", 53, 34, useSpellBank: false));

        var got = before.Map.SpellEffects.Select(Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(want, got);
        Assert.All(before.Map.SpellEffects, r => Assert.Contains(r.ModifierId, before.Map.Units[r.UnitId].Modifiers, StringComparer.OrdinalIgnoreCase));
        static string Key(SpellEffectRecord r) =>
            $"{r.UnitId.ToUpperInvariant()} {r.OriginId.ToUpperInvariant()} {r.ModifierId.ToUpperInvariant()} {r.CasterId.ToUpperInvariant()} {r.TurnStop}";
    }

    /// <summary>Срок (<c>CEffectRemoveSpell</c> <c>0x43b9ef</c>): в тот же день записи живы; в начале хода заклинателя на
    /// следующий день записи уходят, а модификаторы — из цепочек юнитов; чужие записи остаются.</summary>
    [SkippableFact]
    public void Effects_ExpireAtCasterTurn()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-103.sg");
        var map = game.Map;
        var mine = map.SpellEffects.Where(r => Same(r.CasterId, Human)).ToList();
        Assert.NotEmpty(mine);
        Assert.Same(map, game.ExpireEffects(map, Human));

        var next = game.ExpireEffects(GameSession.With(map, turn: map.Turn + 1), Human);
        Assert.DoesNotContain(next.SpellEffects, r => Same(r.CasterId, Human));
        Assert.Equal(map.SpellEffects.Count - mine.Count, next.SpellEffects.Count);
        foreach (var r in mine)
            Assert.Equal(map.Units[r.UnitId].Modifiers.Count(m => Same(m, r.ModifierId)) - mine.Count(x => x.UnitId == r.UnitId && Same(x.ModifierId, r.ModifierId)),
                         next.Units[r.UnitId].Modifiers.Count(m => Same(m, r.ModifierId)));
    }

    /// <summary>Запас хода (<c>0x41c617</c>): «G000SS0087» (<c>RESTORE_M</c> 100, вся карта) — своим отрядам дневной запас, не
    /// выше него (журнал human seq 12219568: 0 → 27 при запасе 27); модификатора категория хода не кладёт.</summary>
    [SkippableFact]
    public void RestoreMove_FillsOwnStacksUpToAllowance()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, Human) && s.Inside.Length == 0);
        var stacks = game.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[own.Id] = own with { Move = 0 };
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, "G000SS0087"] };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, stacks: stacks, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
        int allowance = game.DayAllowance(game.Map.Stacks[own.Id]);
        int effects = game.Map.SpellEffects.Count;

        Assert.Null(game.CastSpell(Human, "G000SS0087", own.X, own.Y, useSpellBank: false));
        Assert.Equal(allowance, game.Map.Stacks[own.Id].Move);
        Assert.Equal(effects, game.Map.SpellEffects.Count);
    }

    /// <summary>Невидимость (<c>0x41c9a5</c>): отряд-цель прячется; нападение показывает его снова (<see cref="GameSession.Unhide"/>).</summary>
    [SkippableFact]
    public void Invisibility_HidesUntilAttack()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.First(s => s.Category == SpellCategory.Invisibility && s.Area == 1);
        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, Human) && s.Inside.Length == 0 && !game.Invisible(s.Id));
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, spell.Id.ToUpperInvariant()],
                                               Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);

        Assert.Null(game.CastSpell(Human, spell.Id, own.X, own.Y, useSpellBank: false));
        Assert.True(game.Invisible(own.Id));
        game.Unhide(own.Id);
        Assert.False(game.Invisible(own.Id));
    }

    /// <summary>Зелье воскрешения лидеру на карте: 1 HP и отряд снова «с живым лидером» (<c>LEADR_ALIV</c>). До 14.10 признак оставался
    /// снятым: ИИ отпаивал павшего лидера (воскрешение, затем лечение), а тактика до конца партии видела отряд без лидера — без атак,
    /// руин и торговцев (Нежить долгой партии порта: 2 боя с нейтралами за 20 дней против 10 у оригинала).</summary>
    [SkippableFact]
    public void RevivePotion_LeaderAliveAgain()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var potion = game.Book!.Items.Values.First(i => i.Category == ItemCategory.PotionRevive);
        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, Human) && s.LeaderAlive);
        const string instance = "S073IMfffd";
        var stacks = game.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[own.Id] = own with { Items = [.. own.Items, instance], LeaderAlive = false };
        var units = game.Map.Units.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        units[own.LeaderId] = units[own.LeaderId] with { Hp = 0 };
        var types = game.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[instance] = potion.Id;
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, stacks: stacks, units: units, itemTypes: types), game.Looks.Stacks) },
                               game.Art, game.Book);

        bool LookAlive() => game.Looks.Stacks.Single(s => Same(s.Id, own.Id)).LeaderAlive;
        Assert.False(LookAlive());

        Assert.Null(game.DrinkPotion(own.Id, instance, own.LeaderId));
        Assert.Equal(1, game.Map.Units[own.LeaderId].Hp);
        Assert.True(game.Map.Stacks[own.Id].LeaderAlive);
        // И облик: значок павшего лидера ICNDEADL (MapScene рисует его по облику) снят — до fix/mapfix оставался над воскрешённым.
        Assert.True(LookAlive());
    }

    /// <summary>Зелье усиления, выпитое на карте, — та же запись со сроком (<c>ORIGIN_ID</c> — тип предмета); в начале следующего
    /// хода хозяина модификатор снимается.</summary>
    [SkippableFact]
    public void BoostPotion_ExpiresNextTurn()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var potion = game.Book!.Items.Values.First(i => i.Category == ItemCategory.PotionBoost && i.PotionModifierId.Length > 0);
        var own = game.Map.Stacks.Values.First(s => Same(s.Owner, Human) && s.LeaderAlive);
        const string instance = "S073IMfffe";
        var stacks = game.Map.Stacks.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        stacks[own.Id] = own with { Items = [.. own.Items, instance] };
        var types = game.Map.ItemTypes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        types[instance] = potion.Id;
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, stacks: stacks, itemTypes: types), game.Looks.Stacks) },
                               game.Art, game.Book);
        int had = game.Map.Units[own.LeaderId].Modifiers.Count(m => Same(m, potion.PotionModifierId));

        Assert.Null(game.DrinkPotion(own.Id, instance, own.LeaderId));
        var record = Assert.Single(game.Map.SpellEffects, r => Same(r.OriginId, potion.Id) && Same(r.UnitId, own.LeaderId));
        Assert.Equal(SpellCast.TurnStop(game.Map.Turn), record.TurnStop);
        var next = game.ExpireEffects(GameSession.With(game.Map, turn: game.Map.Turn + 1), Human);
        Assert.Equal(had, next.Units[own.LeaderId].Modifiers.Count(m => Same(m, potion.PotionModifierId)));
    }

    /// <summary>
    /// Срок зелья усиления на карте по сохранениям: журнал human, день 62 — человек пьёт шесть зелий усиления (<c>potion_use</c>,
    /// seq 15483131…15493737, между сохранениями 102 и 103). Порт повторяет их поверх сохранения 102 — записи
    /// <c>CMidSpellEffects</c> с <c>ORIGIN_ID</c> = тип зелья те же, что в сохранении 103: юнит, модификатор, заклинатель (хозяин
    /// отряда), <c>TURN_STOP</c> = день + 1, как у заклинания.
    /// </summary>
    [SkippableFact]
    public void BoostPotion_MatchesSaveRecords()
    {
        var before = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var after = Load("rastlennaya-vlast-dwarf-t62-103.sg");
        (string Unit, string Item)[] drunk =
        [
            ("S073UN01f8", "S073IM005b"), ("S073UN01cc", "S073IM00c2"), ("S073UN01f1", "S073IM00b0"),
            ("S073UN01f8", "S073IM0057"), ("S073UN01cc", "S073IM0062"), ("S073UN01cc", "S073IM0061"),
        ];
        bool Potion(string origin) => before.Book!.Items.TryGetValue(origin, out var i) && i.Category == ItemCategory.PotionBoost;
        var want = after.Map.SpellEffects.Where(r => Potion(r.OriginId)).Select(Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(drunk.Length, want.Count);

        foreach (var (unit, item) in drunk)
            Assert.Null(before.DrinkPotion("S073KC0001", item, unit));

        Assert.Equal(want, before.Map.SpellEffects.Where(r => Potion(r.OriginId)).Select(Key).Order(StringComparer.Ordinal).ToList());
        static string Key(SpellEffectRecord r) =>
            $"{r.UnitId.ToUpperInvariant()} {r.OriginId.ToUpperInvariant()} {r.ModifierId.ToUpperInvariant()} {r.CasterId.ToUpperInvariant()} {r.TurnStop}";
    }

    /// <summary>Туман (<c>0x41c7f8</c>): другим игрокам квадрат закрывается (кроме того, что видят их отряды и города), заклинателю —
    /// нет.</summary>
    [SkippableFact]
    public void Fog_HidesSquareFromOthers()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.Fog && s.Area is > 1 and < 999);
        Skip.If(spell is null, "нет тумана площадью");
        var map = game.Map;
        var fogMine = map.Fog[map.Players[Human].FogId];
        (int X, int Y) at = Enumerable.Range(0, map.Size).SelectMany(x => Enumerable.Range(0, map.Size).Select(y => (x, y)))
                                      .First(c => !fogMine[c.x, c.y] && c.x > 10 && c.y > 10 && c.x < map.Size - 10 && c.y < map.Size - 10);
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, spell!.Id.ToUpperInvariant()],
                                               Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
        var before = game.Map.Players.Values.ToDictionary(p => p.Id, p => game.Map.Fog.TryGetValue(p.FogId, out var f) ? (bool[,])f.Clone() : null);
        int r = spell.Area / 2 + 1;
        int hiddenOthers = 0;

        Assert.Null(game.CastSpell(Human, spell.Id, at.X, at.Y, useSpellBank: false));

        foreach (var p in game.Map.Players.Values)
        {
            if (before[p.Id] is not { } was || !game.Map.Fog.TryGetValue(p.FogId, out var now)) continue;
            int hidden = 0, opened = 0;
            for (int x = at.X - r; x <= at.X + r; x++)
                for (int y = at.Y - r; y <= at.Y + r; y++)
                {
                    if (!was[x, y] && now[x, y]) hidden++;
                    if (was[x, y] && !now[x, y]) opened++;
                }
            if (Same(p.Id, Human)) Assert.Equal(0, hidden);
            else hiddenOthers += hidden;
            Assert.Equal(0, opened);
        }
        Assert.True(hiddenOthers > 0);
    }

    /// <summary>Смена земли (<c>0x4359a7</c>): в квадрате равнина без объектов и жил рядом становится грунтом заклинания и
    /// нейтральной землёй; клетки с крепостью, водой и горами не меняются; где менять нечего — отказ цели.</summary>
    [SkippableFact]
    public void ChangeTerrain_TurnsPlainIntoSpellGround()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.ChangeTerrain);
        Skip.If(spell is null, "нет заклинания смены земли");
        game = Learn(game, spell!.Id);
        var map = game.Map;
        int half = spell.Area / 2;
        (int X, int Y) at = Enumerable.Range(half + 1, map.Size - 2 * half - 2)
            .SelectMany(x => Enumerable.Range(half + 1, map.Size - 2 * half - 2).Select(y => (x, y)))
            .First(c => Square(c, half).Count(p => map.Tiles[p.X, p.Y].Ground == Ground.Plain && !map.Plan.ContainsKey(p)) >= 1
                        && Square(c, half).Any(p => map.Tiles[p.X, p.Y].Ground is Ground.Water or Ground.Mountain));
        var before = (Tile[,])map.Tiles.Clone();
        (int X, int Y) water = Enumerable.Range(half, map.Size - 2 * half)
            .SelectMany(x => Enumerable.Range(half, map.Size - 2 * half).Select(y => (x, y)))
            .First(c => Square(c, half).All(p => map.Tiles[p.X, p.Y].Ground is Ground.Water or Ground.Mountain));
        Assert.Equal("земля здесь не меняется", game.SpellRefusal(Human, spell.Id, water.X, water.Y, useSpellBank: false));

        Assert.Null(game.CastSpell(Human, spell.Id, at.X, at.Y, useSpellBank: false));

        int changed = 0;
        foreach (var p in Square(at, half))
        {
            var was = before[p.X, p.Y];
            var now = game.Map.Tiles[p.X, p.Y];
            if (was.Ground is Ground.Water or Ground.Mountain) Assert.Equal(was, now);
            if (now != was)
            {
                changed++;
                Assert.Equal(spell.Ground, (int)now.Ground);
                Assert.Equal(5, now.Terrain);
            }
        }
        Assert.True(changed > 0);
    }

    /// <summary>Снос жезла заклинанием: чужой жезл клетки уходит с карты; свой — отказ цели, мана не тратится; без жезла — отказ.</summary>
    [SkippableFact]
    public void RemoveRod_TakesForeignRodOnly()
    {
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.RemoveRod);
        Skip.If(spell is null, "нет заклинания сноса жезла");
        game = Learn(game, spell!.Id);
        var foreign = game.Map.Rods.Values.FirstOrDefault(r => !Same(r.Owner, Human));
        var own = game.Map.Rods.Values.FirstOrDefault(r => Same(r.Owner, Human));
        Skip.If(foreign is null && own is null, "жезлов нет");
        if (own is not null)
        {
            var bank = game.Map.Players[Human].Bank;
            Assert.NotNull(game.CastSpell(Human, spell.Id, own.X, own.Y, useSpellBank: false));
            Assert.Equal(bank, game.Map.Players[Human].Bank);
        }
        if (foreign is not null)
        {
            Assert.Null(game.CastSpell(Human, spell.Id, foreign.X, foreign.Y, useSpellBank: false));
            Assert.False(game.Map.Rods.ContainsKey(foreign.Id));
            Assert.DoesNotContain(foreign.Id, game.Map.Plan.GetValueOrDefault((foreign.X, foreign.Y)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>ИИ у своих крепостей (<c>0x46f1d1</c>): чужой жезл не дальше 7 от входа столицы сносится известным «Sinkhole»
    /// (площадь 1), без бросков R250.</summary>
    [SkippableFact]
    public void AiDefend_RemovesForeignRodNearOwnFort()
    {
        const string Ai = "S073PL0009";
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.RemoveRod && s.Area == 1);
        Skip.If(spell is null, "нет заклинания сноса жезла");
        var map = game.Map;
        var capital = map.Sites.First(s => s.Kind == SiteKind.Capital && Same(s.Owner, Ai));
        var gate = AiTactics.Entrance(capital);
        (int X, int Y) cell = Enumerable.Range(-5, 11).SelectMany(dx => Enumerable.Range(-5, 11).Select(dy => (gate.X + dx, gate.Y + dy)))
            .First(c => map.Contains(c.Item1, c.Item2) && !map.Plan.ContainsKey(c) && map.Tiles[c.Item1, c.Item2].Ground == Ground.Plain);
        var rod = new MapRod("S073RDfff0", Human, cell.X, cell.Y);
        var rods = map.Rods.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        rods[rod.Id] = rod;
        var plan = map.Plan.ToDictionary(p => p.Key, p => p.Value);
        plan[cell] = [rod.Id];
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Ai] = players[Ai] with { KnownSpells = [.. players[Ai].KnownSpells, spell!.Id.ToUpperInvariant()],
                                         Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks,
                                   GameSession.WithRods(GameSession.With(map, players: players, plan: plan), rods), game.Looks.Stacks) },
                               game.Art, game.Book);
        long steps = game.Rng.Steps;

        var report = new List<string>();
        AiSpells.Defend(game, Ai, new AiForces([], []), game.Rng, report);

        Assert.False(game.Map.Rods.ContainsKey(rod.Id));
        Assert.Contains(report, r => r.Contains(spell.Id, StringComparison.OrdinalIgnoreCase) && !r.Contains("отказ", StringComparison.Ordinal));
        Assert.Equal(steps, game.Rng.Steps);
    }

    /// <summary>ИИ у своего жезла (<c>0x46f1d1</c>) без казны: клетки колец проверку не проходят, смена земли идёт командой сервера
    /// в клетку жезла (<c>0x443600</c>) — <c>CEffectSpell</c> отказывает (ни оплаты, ни записи), а эффект земли (слот +4 результата,
    /// <c>0x41cdd5</c>) всё равно меняет равнину квадрата на лес.</summary>
    [SkippableFact]
    public void AiDefend_ChangesTerrainAtOwnRodWithoutTreasury()
    {
        const string Ai = "S073PL0009";
        var game = Load("rastlennaya-vlast-dwarf-t62-102.sg");
        var spell = game.Book!.Spells.Values.FirstOrDefault(s => s.Category == SpellCategory.ChangeTerrain && s.Ground == (int)Ground.Forest);
        Skip.If(spell is null, "нет заклинания смены земли в лес");
        var map = game.Map;
        var capital = map.Sites.First(s => s.Kind == SiteKind.Capital && Same(s.Owner, Ai));
        var gate = AiTactics.Entrance(capital);
        int half = spell!.Area / 2;
        (int X, int Y) cell = Enumerable.Range(-5, 11).SelectMany(dx => Enumerable.Range(-5, 11).Select(dy => (gate.X + dx, gate.Y + dy)))
            .First(c => map.Contains(c.Item1, c.Item2) && !map.Plan.ContainsKey(c) && map.Tiles[c.Item1, c.Item2].Ground == Ground.Plain
                        && Square(c, half).Count(p => map.Contains(p.X, p.Y) && map.Tiles[p.X, p.Y].Ground == Ground.Plain && !map.Plan.ContainsKey(p)) >= 2);
        var rod = new MapRod("S073RDfff0", Ai, cell.X, cell.Y);
        var rods = map.Rods.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        rods[rod.Id] = rod;
        var plan = map.Plan.ToDictionary(p => p.Key, p => p.Value);
        plan[cell] = [rod.Id];
        var players = map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Ai] = players[Ai] with { KnownSpells = [spell.Id.ToUpperInvariant()], Bank = Bank.Zero, SpellBank = Bank.Zero };
        var stacks = map.Stacks.Where(p => Same(p.Value.Owner, Ai) || Math.Max(Math.Abs(p.Value.X - gate.X), Math.Abs(p.Value.Y - gate.Y)) > 20)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        game = new GameSession(game.State with { Looks = GameSession.Retold(game.Looks,
                                   GameSession.WithRods(GameSession.With(map, players: players, plan: plan, stacks: stacks), rods), game.Looks.Stacks) },
                               game.Art, game.Book);
        Assert.Equal("не хватает казны", game.SpellRefusal(Ai, spell.Id, cell.X, cell.Y, useSpellBank: true));
        var before = (Tile[,])game.Map.Tiles.Clone();
        int casts = game.Map.SpellCasts.Count;

        var report = new List<string>();
        AiSpells.Defend(game, Ai, new AiForces([], []), game.Rng, report);

        Assert.Contains(report, r => r.Contains($"({cell.X},{cell.Y}) без оплаты: не хватает казны", StringComparison.Ordinal));
        Assert.Equal(Bank.Zero, game.Map.Players[Ai].Bank);
        Assert.Equal(casts, game.Map.SpellCasts.Count);
        Assert.Contains(Square(cell, half), p => game.Map.Contains(p.X, p.Y) && before[p.X, p.Y].Ground == Ground.Plain
                                                   && game.Map.Tiles[p.X, p.Y].Ground == Ground.Forest);
    }

    private static IEnumerable<(int X, int Y)> Square((int X, int Y) c, int half) =>
        Enumerable.Range(c.X - half, 2 * half + 1).SelectMany(x => Enumerable.Range(c.Y - half, 2 * half + 1).Select(y => (x, y)));

    private static GameSession Learn(GameSession game, string spellId)
    {
        var players = game.Map.Players.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        players[Human] = players[Human] with { KnownSpells = [.. players[Human].KnownSpells, spellId.ToUpperInvariant()],
                                               Bank = new Bank(9999, 9999, 9999, 9999, 9999, 9999) };
        return new GameSession(game.State with { Looks = GameSession.Retold(game.Looks, GameSession.With(game.Map, players: players), game.Looks.Stacks) },
                               game.Art, game.Book);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
