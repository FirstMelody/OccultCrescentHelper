using System.Collections.Generic;

namespace BOCCHI.Modules.Automator;

/// <summary>
/// Known CN 7.55 North Horn field events.
/// Event IDs and localized names were adapted from KanoNoUta/BOCCHI commit
/// 2aa3a03142d8b31302e0d279669ded18b5b1ceec.
/// Live names still take precedence when the client exposes them.
/// </summary>
public static class NorthernEventCatalog
{
    public static IReadOnlyDictionary<uint, string> CriticalEncounters { get; } =
        new Dictionary<uint, string>
        {
            [49] = "四颚斧花——提蔛",
            [50] = "魔女复制体——卡洛菲斯提莉二重身",
            [51] = "纯白守护者——雪石膏之剑",
            [52] = "禁书化形——古术魔典",
            [53] = "暗红尸骸——赤龙",
            [54] = "暴食咒鬼——阿尔戈尔",
            [55] = "残暴的母蜘蛛——新月阿剌克涅",
            [56] = "叛逆使魔——负隅宝石兽",
            [57] = "天道好轮回——魔亡灵法师",
            [58] = "求道的人造人——神木巨人",
            [59] = "诅咒的继承者——惨白魔人",
            [60] = "魔法军团——小小法师",
            [61] = "孤岛的绑架犯——诱拐魔",
            [62] = "苏醒的多头龙——魔许德拉",
            [63] = "拟态使魔——变形法师",
        };

    public static IReadOnlyDictionary<uint, string> Fates { get; } =
        new Dictionary<uint, string>
        {
            [2072] = "被欺负的魔法罐",
            [2073] = "被吹飞的魔法罐",
            [2074] = "暴力牛魔——好战弥诺陶洛斯",
            [2075] = "诅咒宝珠——邪瞳",
            [2076] = "水边暴君——统领奇美拉",
            [2077] = "历战水马——凯尔派总领",
            [2078] = "魔界的叹息——妖艳魔花珊迪",
            [2079] = "自怨自艾的歌手——伊阿姆柏",
            [2080] = "狼占狗窝——遗迹冰狼",
            [2081] = "腐坏街道的守护者——忍耐基路伯",
            [2082] = "驾驭自然的巨兽——呼风狮鹫",
            [2083] = "仿制的蛇人偶——半灵美杜莎",
            [2084] = "高傲的雷兽——新月女王",
        };
}
