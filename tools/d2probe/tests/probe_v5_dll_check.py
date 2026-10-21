"""Проверка собранной DLL зонда на полноту схем 5–7.6 — без запуска игры.

Отвечает на два вопроса, которые иначе выясняются только живым прогоном:

1. попали ли в бинарник имена всех событий схемы 5 (значит, код хуков не выпал
   при сборке и счётчики заведены); для `relation` ищется ещё и сам литерал
   `"ev":"relation"` — голое слово `relation` в DLL есть и без хука (биндинги Lua);
   шесть имён схемы 5.3 в DLL до неё не встречались вовсе (проверено на сборке 5.2),
   поэтому им хватает голого имени, и поле `seq_end` ищется так же. У имён схемы 6.0,
   которые встречаются и как обычные слова (`save`, `scout`), ищется литерал строки;
2. лежат ли в нём все адреса GOG-сборки как непосредственные значения: четырнадцать
   из `probeecon.cpp`, мутатор отношений и девять адресов возврата его таблицы
   причин из `probediplo.cpp`, шесть точек входа причин трат из `probespend.cpp`,
   двадцать четыре точки входа схемы 6.0 (партия 1) из `probemap.cpp`, `probecheck.cpp`,
   `probenoble.cpp`, десять (партия 2) из `probeleader.cpp`, `probecheck.cpp` и двадцать
   `onHit` схемы 6.1 из `probehit.cpp`, три точки входа и два адреса возврата схемы 6.2 из
   `probestate.cpp`, семь точек входа и три адреса возврата схемы 6.3 из `probeunit.cpp` и
   `probecheck.cpp`, шестнадцать точек входа, адреса возврата и vftable подарков схемы 6.4 из
   `probeai.cpp`, четырнадцать точек входа, vftable и адресов возврата схемы 7.0 и ещё четыре схемы 7.1
   из `probeui.cpp` (`ui_clear` с 7.1 не ставится)
   (значит, таблицы доехали до кода, а не были свёрнуты).

Пустой лог живого прогона после этого означает «хук стоял и не сработал», а не
«хука нет» — ровно то разделение, ради которого в зонде есть счётчики без бюджета.

    python tools/d2probe/tests/probe_v5_dll_check.py [путь к mss32.dll]

Код выхода 0 — всё на месте, 1 — чего-то нет (перечислено в выводе).
"""
import pathlib
import struct
import sys

DEFAULT_DLL = pathlib.Path(__file__).resolve().parents[1] / "mss32" / "Release" / "mss32.dll"

# Имена событий схемы 5: probe.cpp (countedEvents), probeecon.cpp, probeinput.cpp.
EVENT_NAMES = [
    "bank", "site_buy_item", "site_sell_item", "site_buy_spell", "site_buy_unit",
    "site_train", "item_equip", "item_unequip", "heal", "revive", "city_grow",
    "build", "thief_roll", "thief_chance", "fort_day", "input", "input_get", "input_pump",
    "relation", '"ev":"relation"',
    # Схема 5.3, probespend.cpp: причины трат казны и их общее поле.
    "hire_unit", "hire_leader", "rod_drop", "spell_cast", "spell_research", "wand_cast",
    '"seq_end":',
    # Схема 6.0, партия 1: probemap.cpp, probecheck.cpp, probenoble.cpp, поле xp и fort_day.
    '"ev":"save"', "fort_capture", "land_grow", "land_reject", "site_visit", "spell_expire",
    "item_create", "ap_effect", "ap_apply", "hp_change", "garrison_max", '"bonus":', "rod_check",
    "hire_check", "hire_leader_check", "merc_check", "sell_check", "grow_check", "noble_action",
    "thief_cancel", "noble_choose", "alliance_propose", "alliance_change", "trade_accept",
    '"ret_addr":"{}","ret":{}', '"protect_b":',
    # Схема 6.0, партия 2: probeleader.cpp, probecheck.cpp, новые поля xp и revive.
    "skill_offer", "skill_pick", "skill_pick_query", "upg_count", "equip_check", "wand_check",
    "potion_use", "item_consume", "talisman_charge", "xp_group", "weapon_master",
    '"daily_uses":', '"max_leader":', '"hp_before":{},"hp_after":{},"xp_before":{}',
    # Схема 6.1: probehit.cpp - цель атак без урона.
    '"ev":"attack_hit"', '"new_seq":', '"mods_before":', "hit_bestow_wards", "hit_summon",
    # Схема 6.2: probestate.cpp - пересев генератора и запас хода.
    '"ev":"rng_seed"', '"index_before":', "rng_reseed", '"ev":"ap_restore"', '"leader_found":',
    "turn_start", "max0_no_leader",
    # Схема 6.3: probeunit.cpp, probecheck.cpp - перенос юнитов и отказы интерфейса.
    '"ev":"unit_transfer"', '"same_group":', "unit_swap", "unit_dismiss", "ai_reorder", "swap_check",
    "dismiss_check", "equip_drop", '"ev":"ui_refusal"', "X005TA0247", "X005TA0211", "dead_leader_transfer",
    "wand_precheck",
    # Схема 6.4: probeai.cpp - ход ИИ на карте.
    '"ev":"ai_break_check"', '"t_eff":', '"ev":"ai_offer_check"', '"ev":"ai_gift_check"', '"kind_roll":',
    '"ev":"ai_answer"', "ai_answer_alliance", "ai_alliance_chance", "ai_answer_trade", "ai_trade_chance",
    '"ev":"ai_strategic_step"', "ai_pass", '"ev":"ai_turn"', "ai_turn_begin", "ai_turn_end",
    '"ev":"ai_order"', "ai_order_cmd", "strategic_orders", '"ev":"ai_budget"', '"ev":"ai_chat"',
    "AI_OFFERS_ALLIANCE", "ai_offer_chance",
    # Схема 6.5: probeaieco.cpp, probeaitac.cpp - экономика и тактика ИИ.
    '"ev":"ai_eval"', '"ev":"ai_candidate"', '"ev":"ai_exec"', '"ev":"ai_budget_spend"',
    '"ev":"ai_city_rank"', '"ev":"ai_leader_role"', '"ev":"ai_leader_pick"', '"ev":"ai_unit_pick"',
    '"ev":"ai_build_pick"', '"ev":"ai_rng"', '"ev":"crt_rand"', '"ev":"ai_tactic_candidate"',
    '"ev":"ai_tactic_adjust"', '"ev":"ai_tactic_pick"', '"ev":"ai_tactic_stack"', '"ev":"ai_tactic_result"',
    '"ev":"ai_rod_cell"', '"ev":"ai_strength"', "ai_exec_dipl_gold", "ai_leader_role_thief",
    "ai_build_temple", "ai_tactic_adjust_set", "eval_hire_leader", "melee_small", "leader_name",
    '"ok_block":', "msvcrt.dll",
    # Схема 6.5.1: ненависть как действие дипломатии и длины векторов у ai_strength.
    "ai_exec_dipl_hate", "dipl_hate", '"p2_n":',
    # Схема 7.0: probeui.cpp - экраны, нажатия, тексты, окна сообщений, вход в партию.
    '"ev":"ui_open"', '"parent_class":', '"ev":"ui_close"', "ui_show", "ui_show_refused", "ui_hide",
    "ui_remove", '"ev":"ui_click"', "ui_button", "ui_toggle", "ui_lbox", '"hotkey":',
    '"ev":"ui_text"', '"opening":', '"ev":"ui_msgbox"', '"last_text_id":', '"ev":"game_phase"',
    "game_enter", "game_leave",
    # Схема 7.1: адрес экрана, класс верхнего, прокрутка списка, безымянные элементы, новая игра/загрузка.
    '"iface":', '"top_class":', '"parent":', '"what":"{}","index":', '"child":', '"area":',
    '"base":"DLG_BATTLE_A"', "game_start", "scroll", "select",
    # Схема 7.2: probeai72.cpp - кэш целей тактики, сила по юнитам, лечение, повышение, A*, выдача id.
    '"ev":"ai_place_cache"', '"ev":"ai_fort_cache"', '"ev":"ai_strength_units"', '"ev":"regen"',
    '"ev":"upgrade_target"', '"ev":"path_expand"', '"ev":"id_alloc"', "ai_tactic_enter", "ai_cache_pick",
    "path_expand_node", "regen_unit_term", "upgrade_leader", '"race_player":', '"callers":',
    # Схема 7.5: probeairng.cpp - все броски решений игры вне показа и боя, цепочка обёрток, origin.
    '"outer":{},"origin":{}', "attack_spell_kind", "attack_spell_pick", "merchant_item",
    "shuffle_list", "op_spell_start", "harass_chance", "spy_discover", "event_chance", "answer_trade",
    "new_game_desc", "mage", "gift_spell", "harass_far",
    # Схема 7.6: probeflash.cpp, probemarks.cpp, probeui.cpp - портреты боя, метки карты, погашенные кнопки.
    '"schema":"7.6"', '"ev":"bat_flash"', "bat_flash_end", "bat_text_end", "bat_flash_set", "bat_flash_rec", "bat_flash_tick",
    "bat_text_xp", "bat_text_img", '"persist":{},"on":{},"found":{}', '"ev":"map_sel"', '"ev":"map_path"', "map_sel_clear",
    "map_path_clear", "map_path_img", '"ev":"ui_enable"', "xp_up",
]

# Точки входа GOG 3.01, ImageBase 0x400000 — те же значения, что в probeecon.cpp.
GOG_ADDRESSES = {
    "CVisitorChangePlayerBank::apply": 0x60BE8D,
    "CVisitorPlayerAddCapitalBuilding::apply": 0x60C496,
    "onSiteBuyItem": 0x5D76E2,
    "onSiteSellItem": 0x5D77F5,
    "onSiteBuySpell": 0x5D7952,
    "onSiteBuyUnit": 0x5D7AA1,
    "onSiteTrainUnit": 0x5D7C6B,
    "equipItem": 0x5E741C,
    "unEquipItem": 0x5E746D,
    "healUnit": 0x5D724E,
    "reviveUnit": 0x5D733F,
    "growCity": 0x5DA01E,
    "thiefRoll": 0x443067,
    "thiefChance": 0x5D31A1,
    # Схема 5.2, probediplo.cpp: мутатор отношений и адреса возврата девяти точек вызова.
    "changeRelations": 0x5D9366,
    "relation<-tribute": 0x5D935E,
    "relation<-alliance_make": 0x5D96E4,
    "relation<-attack": 0x5D9828,
    "relation<-rod_destroyed": 0x5D98A9,
    "relation<-alliance_break": 0x5D98DA,
    "relation<-alliance_refuse": 0x5D9961,
    "relation<-noble": 0x5D99BA,
    "relation<-trade_item": 0x5D9A2A,
    "relation<-trade_spell": 0x5D9ABE,
    # Схема 5.3, probespend.cpp: точки входа путей, которые двигают казну без прежнего события.
    "hireUnit": 0x5D565F,
    "hireLeader": 0x5D6957,
    "CEffectDropRod::apply": 0x437606,
    "castSpell (<- CEffectSpell::apply)": 0x5DC2D6,
    "researchSpell (<- CEffectSpellResearch::apply)": 0x5DC1D7,
    "castWand (<- CEffectWand::apply)": 0x5DC4D6,
    # Схема 6.0, партия 1: probemap.cpp.
    "save: CMidServerLogicCore::saveGame": 0x429F3F,
    "fort_capture: CEffectCapture::apply": 0x433E2E,
    "land_grow": 0x5E0551,
    "land_reject": 0x5E07B2,
    "site_visit": 0x5D7664,
    "spell_expire: CEffectRemoveSpell::apply": 0x43B9EF,
    "item_create": 0x5E732F,
    "ap_effect: CEffectChangeAP ctor": 0x43588C,
    "ap_apply: CEffectChangeAP::apply": 0x4358D4,
    "hp_change: CEffectChangeHP::apply": 0x436299,
    # probecheck.cpp.
    "garrison_max": 0x6144CE,
    "scout: CUmStack::getScout": 0x5A38E8,
    "rod_check": 0x5D9E59,
    "hire_check": 0x5DEEA7,
    "hire_leader_check": 0x5DE921,
    "merc_check": 0x5E00C6,
    "sell_check": 0x5DFE1A,
    "grow_check": 0x5DC62A,
    # probenoble.cpp.
    "noble_action: CEffectNobleAction::apply": 0x43A335,
    "thief_cancel": 0x443506,
    "noble_choose: CEffectNobleChoose::apply": 0x43A5A7,
    "alliance_propose: CEffectProposeAlliance::apply": 0x43B5C8,
    "alliance_change: CEffectChangeAlliance::apply": 0x43574E,
    "trade_accept: CEffectAcceptTrade::apply": 0x433805,
    # Схема 6.0, партия 2: probeleader.cpp.
    "skill_offer": 0x5D8B84,
    "skill_pick: visitorLeaderUpgrade": 0x5E84C2,
    "upg_count: visitorLeaderUpgradeCount": 0x5E8513,
    "potion_use: applyPotion": 0x5D70F9,
    "item_consume": 0x629824,
    "talisman_charge": 0x5E74BB,
    "xp_group: CBatAttackGroupUpgrade::upgradeGroup": 0x665217,
    "weapon_master": 0x665851,
    # probecheck.cpp.
    "equip_check": 0x5DF6B7,
    "wand_check": 0x5DBDD2,
    # Схема 6.1, probehit.cpp: IBatAttack::onHit (слот 12) двадцати классов без урона.
    "attack_hit: Paralyze::onHit": 0x65AE85,
    "attack_hit: Petrify::onHit": 0x65B207,
    "attack_hit: Heal::onHit": 0x65C417,
    "attack_hit: Fear::onHit": 0x65C76F,
    "attack_hit: BoostDamage::onHit": 0x65CDCA,
    "attack_hit: BestowWards::onHit": 0x65D528,
    "attack_hit: Shatter::onHit": 0x65D8DC,
    "attack_hit: LowerDamage::onHit": 0x65DC91,
    "attack_hit: LowerInitiative::onHit": 0x65E008,
    "attack_hit: Poison::onHit": 0x65E39D,
    "attack_hit: Frostbite::onHit": 0x65E9C4,
    "attack_hit: Blister::onHit": 0x65EFBD,
    "attack_hit: Revive::onHit": 0x65F4DE,
    "attack_hit: Cure::onHit": 0x6600C8,
    "attack_hit: GiveAttack::onHit": 0x6603DC,
    "attack_hit: TransformOther::onHit": 0x660845,
    "attack_hit: TransformSelf::onHit": 0x6613E1,
    "attack_hit: Doppelganger::onHit": 0x6621CD,
    "attack_hit: DrainLevel::onHit": 0x662880,
    "attack_hit: Summon::onHit": 0x6631EB,
    # Схема 6.2, probestate.cpp.
    "rng_seed: setState": 0x548473,
    "rng_reseed: reseed": 0x5484BD,
    "rng_seed: возврат setState из reseed": 0x548505,
    "ap_restore: changeStackMove": 0x5E780D,
    "ap_restore: возврат turn_start": 0x5D4470,
    # Схема 6.3, probeunit.cpp и probecheck.cpp.
    "unit_swap: 0x5d5642": 0x5D5642,
    "unit_dismiss: 0x5d5773": 0x5D5773,
    "swap_check": 0x5DF4FB,
    "dismiss_check": 0x5DF266,
    "equip_drop: CDDEquipment слот 9": 0x504782,
    "ui_refusal: getInterfaceText": 0x5C7F06,
    "wand_precheck": 0x5DBBE8,
    "unit_transfer: возврат swap_msg": 0x42E45A,
    "unit_transfer: возврат ai_reorder": 0x5D642F,
    "unit_transfer: возврат dismiss_msg": 0x42E726,
    # Схема 6.4, probeai.cpp.
    "ai_break_check": 0x448897,
    "ai_offer_check": 0x448AAD,
    "ai_offer_chance": 0x448AE2,
    "ai_gift_check": 0x448BDA,
    "ai_answer_alliance": 0x474B37,
    "ai_alliance_chance": 0x474C21,
    "ai_answer_trade": 0x474841,
    "ai_trade_chance": 0x4749C8,
    "ai_strategic_step": 0x44C825,
    "ai_pass": 0x44E777,
    "ai_turn_begin": 0x4719F8,
    "ai_turn_end: EndTurn": 0x41E229,
    "ai_order": 0x5E811A,
    "ai_order_cmd": 0x41F21D,
    "ai_budget": 0x44661C,
    "ai_chat": 0x4733A4,
    "ai_break_check: число крепостей 0x4489f6": 0x4489F6,
    "ai_pass: возврат begin": 0x44E757,
    "ai_pass: возврат после execute": 0x44EAD1,
    "ai_turn: возврат ai": 0x471BD5,
    "ai_order: возврат strategic_orders": 0x44E6CC,
    "ai_chat: возврат strategic": 0x44EBA7,
    # switch по vftable подарка свёрнут в вычитание от 0x6cf5dc: остальные две vftable литералами не лежат.
    "ai_gift_check: vftable CAiDiploItem": 0x6CF5DC,
    # Схема 6.5, probeaieco.cpp и probeaitac.cpp.
    "ai_eval": 0x44DCB8,
    "ai_candidate": 0x44DD87,
    "ai_exec_heal": 0x449FCB,
    "ai_exec_dipl_gold": 0x449307,
    "ai_budget_spend": 0x4464EF,
    "ai_city_rank": 0x44809C,
    "ai_leader_role": 0x44B5C0,
    "ai_leader_pick": 0x44AD0F,
    "ai_unit_pick": 0x44C06A,
    "ai_build_pick_s2": 0x446EA3,
    "ai_build_temple": 0x5DCC28,
    "ai_tactic_candidate": 0x4686CF,
    "ai_tactic_adjust": 0x466D5C,
    "ai_tactic_adjust_set": 0x466CD8,
    "ai_tactic_pick": 0x466388,
    "ai_tactic_result": 0x461FB2,
    "ai_rod_cell": 0x46164D,
    "ai_strength": 0x5DDA25,
    "ai_rng: возврат leader_pick": 0x44AEE6,
    "ai_rng: возврат unit_pick": 0x44C13C,
    "ai_eval: возврат hire_unit": 0x44BE46,
    "ai_eval: vftable eval_hire_leader": 0x6CF71C,
    "ai_rod_cell: проверка 0x5cd11f": 0x5CD11F,
    # Схема 6.5.1: CAiDiploHatePlayer - vftable таблицы разбора и её execute.
    "ai_candidate: vftable dipl_hate": 0x6CF5C4,
    "ai_exec_dipl_hate": 0x449180,
    # Схема 7.0: probeui.cpp - точки входа, vftable диалога и адреса возврата обёрток.
    "ui_open: воронка диалогов": 0x5C8110,
    "ui_open: возврат обёртки 0x5c80bf": 0x5C80DB,
    "ui_open: возврат обёртки 0x5c80df": 0x5C810C,
    "ui_close: деструктор CDialogInterf": 0x50B6C6,
    "CDialogInterf vftable": 0x6DF82C,
    "ui_show": 0x53CAC4,
    "ui_remove": 0x53CB64,
    "ui_hide": 0x53CBD1,
    "ui_button: CButtonInterf слот 44": 0x5318E1,
    "ui_toggle: CToggleButton слот 37": 0x534BA3,
    "ui_lbox: подтверждение строки": 0x535F0C,
    "ui_msgbox: CMidgardMsgBox": 0x5C7771,
    "game_enter: CPhaseGame": 0x40529F,
    "game_leave: ~CPhaseGame": 0x40566C,
    # Схема 7.1: открытие сценария сервером, vftable переключателя, возвраты в 0x535f0c.
    "game_start: открытие сценария": 0x429603,
    "ui_button: vftable CToggleButton": 0x6E15A4,
    "ui_lbox: возврат прокрутки": 0x535F8A,
    "ui_lbox: возврат щелчка по строке": 0x536168,
    # Схема 7.2, probeai72.cpp: точки входа и адреса возврата петель лечения.
    "ai_tactic_enter": 0x46586C,
    "ai_cache: CMidServerLogicData2": 0x45E4C7,
    "ai_cache_forts": 0x45EB14,
    "ai_cache_places": 0x45EFA4,
    "ai_fort_filter": 0x45EC77,
    "ai_fort_guard": 0x45ED86,
    "ai_cache_pick": 0x45F793,
    "ai_strength_group": 0x5DCEC7,
    "ai_strength_unit": 0x5DD06C,
    "regen: healUnitByPercent": 0x5D4099,
    "regen_fort_term": 0x5D4181,
    "regen_unit_term": 0x5D4588,
    "regen: возврат гостя крепости": 0x5D4156,
    "regen: возврат отрядов игрока": 0x5D43F6,
    "upgrade_target": 0x5D7FE7,
    "upgrade_leader": 0x5D89C6,
    "path_expand: искатель пути": 0x5CC6DA,
    "path_expand_node: раскрытие узла A*": 0x680786,
    "id_alloc: CMidgardScenarioMap слот 9": 0x5EE8BD,
    # Схема 7.5, probeairng.cpp: адреса возврата мест бросков и обёртки цепочки outer.
    "ai_rng: возврат attack_spell_kind": 0x472350,
    "ai_rng: возврат attack_spell_pick": 0x472415,
    "ai_rng: возврат merchant_item": 0x46B805,
    "ai_rng: возврат shuffle_list": 0x4495FA,
    "ai_rng: обёртка 0x4495d8": 0x4495D8,
    "ai_rng: обёртка 0x46a9f5": 0x46A9F5,
    "ai_rng: origin mage": 0x46A8E2,
    "ai_rng: возврат new_game_desc": 0x42AFA0,
    # Схема 7.6, probeflash.cpp: точки входа портретов панели боя и адреса возврата источников.
    "bat_text: надпись портрета": 0x64D09C,
    "bat_text_xp: стойкая надпись опыта": 0x64D23E,
    "bat_text_img: картинка надписи": 0x64D160,
    "bat_flash: вспышка портрета": 0x64CEE8,
    "bat_flash_set: стойкое состояние": 0x64D2C3,
    "bat_flash_rec: снятие счётчика": 0x64C24C,
    "bat_flash_tick: тик портрета": 0x64D467,
    "bat_flash: возврат итога по цели": 0x63BFAD,
    "bat_flash: возврат конца вспышки": 0x64D3DA,
    "bat_text: место большой надписи": 0x6EBED8,
    # probemarks.cpp: метки карты.
    "map_sel: ShowImageOnMap": 0x5AD1EE,
    "map_sel_clear: HideLayerImages": 0x5AD312,
    "map_path: ShowMovementPath": 0x4CCC2C,
    "map_path_clear": 0x4CD329,
    "map_path_img: картинка по имени": 0x5AE20C,
    "map_path_img: возврат флажка": 0x4CCE7E,
    "map: слой SYM_STACK_SEL": 0x838538,
    "map: слой SYM_MOVE_PATH": 0x838590,
    # probeui.cpp.
    "ui_enable: CButtonInterf::setEnabled": 0x531CCA,
}


def main(path):
    if not path.is_file():
        print("нет файла: %s" % path)
        return 1

    data = path.read_bytes()
    missing = []

    for name in EVENT_NAMES:
        found = name.encode() in data
        print("%s  имя события %s" % ("OK  " if found else "НЕТ ", name))
        if not found:
            missing.append(name)

    for name, address in GOG_ADDRESSES.items():
        found = struct.pack("<I", address) in data
        print("%s  адрес 0x%06x  %s" % ("OK  " if found else "НЕТ ", address, name))
        if not found:
            missing.append("0x%06x" % address)

    if missing:
        print("не найдено: %s" % ", ".join(missing))
        return 1

    print("все %d имён и %d адресов на месте" % (len(EVENT_NAMES), len(GOG_ADDRESSES)))
    return 0


if __name__ == "__main__":
    sys.exit(main(pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_DLL))
