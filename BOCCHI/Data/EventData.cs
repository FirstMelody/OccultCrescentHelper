using System.Collections.Generic;
using System.Numerics;
using BOCCHI.Enums;

namespace BOCCHI.Data;

public struct EventData
{
    public uint Id;

    public EventType Type;

    public string InternalName;

    public Demiatma? Demiatma;

    public SoulShard? Soulshard;

    public MonsterNote? Note;

    public Aethernet? Aethernet;

    public Vector3? StartPosition;

    public float? Radius;

    public readonly static Dictionary<uint, EventData> Fates = new()
    {
        {
            1962,
            new EventData
            {
                Id = 1962,
                Type = EventType.Fate,
                InternalName = "涌潮海魔————纳木",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(162.00f, 56.00f, 676.00f),
            }
        },
        {
            1963,
            new EventData
            {
                Id = 1963,
                Type = EventType.Fate,
                InternalName = "古代怪石——金色石碑",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(373.20f, 70.00f, 486.00f),
            }
        },
        {
            1964,
            new EventData
            {
                Id = 1964,
                Type = EventType.Fate,
                InternalName = "悲鸣收集者——罗普罗斯",
                Demiatma = Enums.Demiatma.Orpiment,
                StartPosition = new Vector3(-226.10f, 116.38f, 254.00f),
            }
        },
        {
            1965,
            new EventData
            {
                Id = 1965,
                Type = EventType.Fate,
                InternalName = "甲板清扫者——巨大鸟",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
                StartPosition = new Vector3(-548.50f, 3.00f, -595.00f),
            }
        },
        {
            1966,
            new EventData
            {
                Id = 1966,
                Type = EventType.Fate,
                InternalName = "神罚石兽——西西弗斯",
                Demiatma = Enums.Demiatma.Malachite,
                StartPosition = new Vector3(-223.10f, 107.00f, 36.00f),
            }
        },
        {
            1967,
            new EventData
            {
                Id = 1967,
                Type = EventType.Fate,
                InternalName = "进化的毒鸟——高等魔鸟",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
                StartPosition = new Vector3(-48.10f, 111.76f, -320.00f),
            }
        },
        {
            1968,
            new EventData
            {
                Id = 1968,
                Type = EventType.Fate,
                InternalName = "湿度猎手——除湿之火",
                Demiatma = Enums.Demiatma.Verdigris,
                StartPosition = new Vector3(-370.00f, 75.00f, 650.00f),
            }
        },
        {
            1969,
            new EventData
            {
                Id = 1969,
                Type = EventType.Fate,
                InternalName = "土壤守护者——癫泥怪",
                Demiatma = Enums.Demiatma.Verdigris,
                StartPosition = new Vector3(-589.10f, 96.50f, 333.00f),
            }
        },
        {
            1970,
            new EventData
            {
                Id = 1970,
                Type = EventType.Fate,
                InternalName = "监视之瞳——岛屿监视者",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(-71.00f, 71.31f, 557.00f),
            }
        },
        {
            1971,
            new EventData
            {
                Id = 1971,
                Type = EventType.Fate,
                InternalName = "美丽的咒杀者——执行者",
                Demiatma = Enums.Demiatma.Orpiment,
                StartPosition = new Vector3(79.00f, 97.86f, 278.00f),
            }
        },
        {
            1972,
            new EventData
            {
                Id = 1972,
                Type = EventType.Fate,
                InternalName = "凶恶使魔————生命收割者",
                Demiatma = Enums.Demiatma.CaputMortuum,
                StartPosition = new Vector3(413.00f, 96.00f, -13.00f),
            }
        },
        {
            1976,
            new EventData
            {
                Id = 1976,
                Type = EventType.Fate,
                InternalName = "幸福的魔法罐",
                Demiatma = Enums.Demiatma.Orpiment,
                Note = MonsterNote.PersistentPots,
                StartPosition = new Vector3(200.00f, 111.73f, -215.00f),
            }
        },
        {
            1977,
            new EventData
            {
                Id = 1977,
                Type = EventType.Fate,
                InternalName = "瑟瑟发抖的魔法罐",
                Demiatma = Enums.Demiatma.Verdigris,
                Note = MonsterNote.PersistentPots,
                StartPosition = new Vector3(-481.00f, 75.00f, 528.00f),
            }
        },
    };

    public readonly static Dictionary<uint, EventData> CriticalEncounters = new()
    {
        {
            48,
            new EventData
            {
                Id = 48,
                Type = EventType.CriticalEncounter,
                InternalName = "两歧塔 血之塔",
            }
        },
        {
            33,
            new EventData
            {
                Id = 33,
                Type = EventType.CriticalEncounter,
                InternalName = "脑髓爱好者——夺心魔",
                Demiatma = Enums.Demiatma.Azurite,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            34,
            new EventData
            {
                Id = 34,
                Type = EventType.CriticalEncounter,
                InternalName = "黑色连队",
                Demiatma = Enums.Demiatma.Orpiment,
                Soulshard = SoulShard.Ranger,
                Note = MonsterNote.BlackChocobos,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            35,
            new EventData
            {
                Id = 35,
                Type = EventType.CriticalEncounter,
                InternalName = "愤怒的人造人——新月狂战士",
                Demiatma = Enums.Demiatma.Azurite,
                Soulshard = SoulShard.Berserker,
                Note = MonsterNote.CrescentBerserker,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            36,
            new EventData
            {
                Id = 36,
                Type = EventType.CriticalEncounter,
                InternalName = "潜影撕裂者——死亡爪",
                Demiatma = Enums.Demiatma.Azurite,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            37,
            new EventData
            {
                Id = 37,
                Type = EventType.CriticalEncounter,
                InternalName = "挣脱封印的大妖异——回廊恶魔",
                Demiatma = Enums.Demiatma.Verdigris,
                Note = MonsterNote.CloisterDemon,
                Aethernet = Enums.Aethernet.Stonemarsh,
            }
        },
        {
            38,
            new EventData
            {
                Id = 38,
                Type = EventType.CriticalEncounter,
                InternalName = "拟造使魔——水晶龙",
                Demiatma = Enums.Demiatma.Malachite,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
            }
        },
        {
            39,
            new EventData
            {
                Id = 39,
                Type = EventType.CriticalEncounter,
                InternalName = "双极的造物——神秘土偶",
                Demiatma = Enums.Demiatma.Malachite,
                Note = MonsterNote.MythicIdol,
                Aethernet = Enums.Aethernet.Stonemarsh,
            }
        },
        {
            40,
            new EventData
            {
                Id = 40,
                Type = EventType.CriticalEncounter,
                InternalName = "石制骑士团",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Aethernet = Enums.Aethernet.BaseCamp,
            }
        },
        {
            41,
            new EventData
            {
                Id = 41,
                Type = EventType.CriticalEncounter,
                InternalName = "传说中的鲨鱼——尼姆瓣齿鲨",
                Demiatma = Enums.Demiatma.Realgar,
                Note = MonsterNote.NymianPotaladus,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            42,
            new EventData
            {
                Id = 42,
                Type = EventType.CriticalEncounter,
                InternalName = "双足狮人——跃立狮",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Soulshard = SoulShard.Oracle,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            43,
            new EventData
            {
                Id = 43,
                Type = EventType.CriticalEncounter,
                InternalName = "防卫指令",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            44,
            new EventData
            {
                Id = 44,
                Type = EventType.CriticalEncounter,
                InternalName = "厌鸟巨兽——进化加鲁拉",
                Demiatma = Enums.Demiatma.Orpiment,
                Aethernet = Enums.Aethernet.BaseCamp,
            }
        },
        {
            45,
            new EventData
            {
                Id = 45,
                Type = EventType.CriticalEncounter,
                InternalName = "贩卖诅咒的商贩——金钱龟",
                Demiatma = Enums.Demiatma.Realgar,
                Note = MonsterNote.TradeTortoise,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            46,
            new EventData
            {
                Id = 46,
                Type = EventType.CriticalEncounter,
                InternalName = "城塞守卫——复原狮像",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            47,
            new EventData
            {
                Id = 47,
                Type = EventType.CriticalEncounter,
                InternalName = "昏暗妖魂——鬼火苗",
                Demiatma = Enums.Demiatma.Malachite,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
            }
        },
    };
}
