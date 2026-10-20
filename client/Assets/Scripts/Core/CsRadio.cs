using System.Collections.Generic;

namespace Cs16.Core
{
    /// <summary>
    /// 无线电**组**（原版 <c>radio1</c> / <c>radio2</c> / <c>radio3</c>，键 Z / X / C）。
    ///
    /// <para>组名与键位的出处：键位 = 原版 <c>cstrike__resource__cstrike_english.txt:1588</c>
    /// 「use the Radio keys (default Z, X, and C)」；本地化名 = 同文件
    /// <c>Cstrike_Standard_Radio / Cstrike_Group_Radio / Cstrike_Report_Radio</c>（<c>:9-11</c>）。</para>
    /// </summary>
    public enum CsRadioSet
    {
        /// <summary>radio1 = Z 组（原版 "Standard radio messages"）。</summary>
        Radio1 = 0,
        /// <summary>radio2 = X 组（原版 "Group radio messages"）。</summary>
        Radio2 = 1,
        /// <summary>radio3 = C 组（原版 "Report radio messages"）。</summary>
        Radio3 = 2,
    }

    /// <summary>一条无线电消息（原版 <c>EVENT_RADIO_*</c> 之一）。</summary>
    public struct CsRadioLine
    {
        /// <summary>本工程消息号（按原版枚举顺序 1..21；<b>不是</b>网络消息号 —— 本工程无网络层）。</summary>
        public int Id;

        /// <summary>所属组。</summary>
        public CsRadioSet Set;

        /// <summary>组内槽位（原版菜单数字键 1..9；出处 <c>cs_bot_radio.cpp:327-347</c>）。</summary>
        public int Slot;

        /// <summary>原版枚举标识（<c>GameEvent.h</c> 里的 <c>EVENT_RADIO_*</c>），便于逐条回查。</summary>
        public string EventName;

        /// <summary>原版文本（逐字，未改标点）。</summary>
        public string Text;

        /// <summary>原版触发条件（一行；出处见 <see cref="CsRadio.Source"/>）。</summary>
        public string Trigger;

        /// <summary>语音资源路径（相对 <c>Resources/</c>，不带扩展名）；没有该条语音的载体时记
        /// <see cref="NoVoice"/>。已解出的 8 条见 <see cref="CsRadio.VoiceOf"/>。</summary>
        public string Voice;

        /// <summary>出处（<c>文件:行</c>）。</summary>
        public string Source;

        /// <summary>组别显示名（原版本地化名）。</summary>
        public string SetLabel => CsRadio.SetLabel(Set);

        /// <summary>没有语音载体时的统一标记（原版 <c>sound/radio/**</c> 未解出这一条）。</summary>
        public const string NoVoice = "原版无（本机无 wav 载体）";
    }

    /// <summary>
    /// 无线电消息表（**原版全部 21 条**，1:1，不增不减）。
    ///
    /// <para><b>枚举出处</b>：原版 <c>GameEventType</c> 的三个无线电段
    /// <c>EVENT_START_RADIO_1 … EVENT_END_RADIO</c>，共 6 + 6 + 9 = 21 条
    /// （<c>原版资源/cs16src/regamedll/game_shared/GameEvent.h:104-128</c>）。
    /// 组与槽的对应 = 原版 <c>CCSBot::SendRadioMessage</c>
    /// （<c>原版资源/cs16src/regamedll/dlls/bot/cs_bot_radio.cpp:327-347</c>）：
    /// <c>slot = '0' + event - EVENT_START_RADIO_n</c> 且 <c>ClientCommand("radioN")</c>。</para>
    ///
    /// <para><b>文本出处</b>：<c>原版资源/cs16src/cstrike/cstrike__resource__cstrike_english.txt</c>
    /// 的 <c>Cstrike_TitlesTXT_*</c> 行，逐字搬运（含大小写与标点）。
    /// HUD 行的格式 <c>"%s1 (RADIO): %s2"</c> 出自同文件 <c>:1748</c> 的 <c>Game_radio</c>。</para>
    ///
    /// <para><b>语音</b>：原版 bot 语音由 <c>bot_phrases.txt</c> 驱动
    /// （<c>cs_bot_chatter.cpp:584-606</c> 解析 <c>Radio &lt;event&gt;</c>、<c>:1129</c> / <c>:1168</c> 播 <c>*.wav</c>）。
    /// 本工程已解出 <b>8 条</b>语音到 <c>Resources/Sound/radio/</c>，由 <see cref="CsRadio.VoiceOf"/> 与
    /// <see cref="CsRadioLine.Voice"/> 指向；其余 13 条没有载体 ⇒ 记 <see cref="CsRadioLine.NoVoice"/>，
    /// 绝不自造音频。</para>
    /// </summary>
    public static class CsRadio
    {
        /// <summary>一条无线电占用的时长（秒）。出处：<c>cs_bot_chatter.cpp:1121</c>、<c>:1163</c>
        /// （发标准无线电时 <c>duration = 2.0f</c>）。</summary>
        public const float LineSeconds = 2f;

        /// <summary>两句之间的额外间隔（秒）。出处：<c>cs_bot_chatter.cpp:1175-1176</c>（<c>const float gap = 0.1f</c>）。</summary>
        public const float GapSeconds = 0.1f;

        /// <summary>同队任意两条无线电的最小间隔（秒）。原版为 <see cref="LineSeconds"/> + <see cref="GapSeconds"/> = 2.1s。
        /// <para>本项目取 <b>12s</b>：8 只 bot 各按原版 2.1s 间隔发言时一回合内会轮番刷屏，
        /// 12s 才能让队内无线电保持稀疏。</para></summary>
        public const float TeamMinInterval = 12f;

        /// <summary>同一条消息的最小重复间隔（秒）。出处：<c>cs_bot_chatter.cpp:1798</c>（<c>NeedBackup</c>）、
        /// <c>:1830</c>（<c>PinnedDown</c>）、<c>:1915</c>（<c>GoingToPlantTheBomb</c>），原版 <c>10.0f</c>。
        /// <para>本项目取 <b>30s</b>：同一句台词按原版 10s 复读过于扎眼，30s 才能压掉复读感。</para></summary>
        public const float SameLineInterval = 30f;

        /// <summary>触发"报位置"的无线电静默阈值（秒）。出处：<c>cs_bot_chatter.cpp:1459</c>（原版 <c>const float longTime = 30.0f</c>）；
        /// 本项目收紧为 60s，减少普通 "Reporting in" 频率。</summary>
        public const float ReportInSilence = 60f;

        /// <summary>"附近"= 最近 <c>recentTime</c> 秒内看见过。出处：<c>cs_bot_vision.cpp:888</c>（<c>const float recentTime = 3.0f</c>）。</summary>
        public const float PerceivedRecentSeconds = 3f;

        /// <summary>"近期见过敌人"的窗口（秒）。出处：<c>cs_bot_chatter.cpp:1775</c>（<c>const float recentTime = 10.0f</c>）。</summary>
        public const float EnemyRecentSeconds = 10f;

        private static readonly CsRadioLine[] Table =
        {
            // ---------- 组 A = radio1（Z）----------
            new CsRadioLine { Id = 1, Set = CsRadioSet.Radio1, Slot = 1, EventName = "EVENT_RADIO_COVER_ME",
                Text = "Cover Me!",
                Trigger = "前往掩护请求支援的队友（NeedBackup 附带 BotHelpMeme）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:105 / cstrike_english.txt:715 / cs_bot_chatter.cpp:1796-1825" },
            new CsRadioLine { Id = 2, Set = CsRadioSet.Radio1, Slot = 2, EventName = "EVENT_RADIO_YOU_TAKE_THE_POINT",
                Text = "You Take the Point.",
                Trigger = "指派尖兵（meme 派发）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:106 / cstrike_english.txt:1027" },
            new CsRadioLine { Id = 3, Set = CsRadioSet.Radio1, Slot = 3, EventName = "EVENT_RADIO_HOLD_THIS_POSITION",
                Text = "Hold This Position.",
                Trigger = "收到命令后 SetTask(HOLD_POSITION) + Hide()（人类指挥）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:107 / cstrike_english.txt:834 / cs_bot_radio.cpp:146-155" },
            new CsRadioLine { Id = 4, Set = CsRadioSet.Radio1, Slot = 4, EventName = "EVENT_RADIO_REGROUP_TEAM",
                Text = "Regroup Team.",
                Trigger = "人类指挥 → Follow",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:108 / cstrike_english.txt:896 / cs_bot_radio.cpp:117-125" },
            new CsRadioLine { Id = 5, Set = CsRadioSet.Radio1, Slot = 5, EventName = "EVENT_RADIO_FOLLOW_ME",
                Text = "Follow Me.",
                Trigger = "人类指挥 → Follow",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:109 / cstrike_english.txt:739 / cs_bot_radio.cpp:114-125" },
            new CsRadioLine { Id = 6, Set = CsRadioSet.Radio1, Slot = 6, EventName = "EVENT_RADIO_TAKING_FIRE",
                Text = "Taking Fire...Need Assistance!",
                Trigger = "交战中被压制（PinnedDown；原版注释：this is a form of \"need backup\"，10.0s 一次）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:110 / cstrike_english.txt:979 / cs_bot_chatter.cpp:1827-1847" },

            // ---------- 组 B = radio2（X）----------
            new CsRadioLine { Id = 7, Set = CsRadioSet.Radio2, Slot = 1, EventName = "EVENT_RADIO_GO_GO_GO",
                Text = "Go go go!",
                Trigger = "宣布推进（人类指挥时为 Hunt()；bot 侧对应 GoingToPlantTheBomb 的 REPORT_CRITICAL_EVENT + BotFollowMeme）",
                Voice = "Sound/radio/com_go",
                Source = "GameEvent.h:112 / cstrike_english.txt:778 / cs_bot_radio.cpp:156-164 / cs_bot_chatter.cpp:1910-1926" },
            new CsRadioLine { Id = 8, Set = CsRadioSet.Radio2, Slot = 2, EventName = "EVENT_RADIO_TEAM_FALL_BACK",
                Text = "Team, fall back!",
                Trigger = "人类指挥 → TryToRetreat()",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:113 / cstrike_english.txt:983 / cs_bot_radio.cpp:140-145" },
            new CsRadioLine { Id = 9, Set = CsRadioSet.Radio2, Slot = 3, EventName = "EVENT_RADIO_STICK_TOGETHER_TEAM",
                Text = "Stick together, team.",
                Trigger = "人类指挥 → Follow",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:114 / cstrike_english.txt:957 / cs_bot_radio.cpp:116-125" },
            new CsRadioLine { Id = 10, Set = CsRadioSet.Radio2, Slot = 4, EventName = "EVENT_RADIO_GET_IN_POSITION_AND_WAIT",
                Text = "Get in position and wait for my go.",
                Trigger = "分工等待（meme 体系）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:115 / cstrike_english.txt:775" },
            new CsRadioLine { Id = 11, Set = CsRadioSet.Radio2, Slot = 5, EventName = "EVENT_RADIO_STORM_THE_FRONT",
                Text = "Storm the Front!",
                Trigger = "人类指挥 → Hunt()",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:116 / cstrike_english.txt:958 / cs_bot_radio.cpp:157-164" },
            new CsRadioLine { Id = 12, Set = CsRadioSet.Radio2, Slot = 6, EventName = "EVENT_RADIO_REPORT_IN_TEAM",
                Text = "Report in, team.",
                Trigger = "人类发报位置命令（唯一不打断正在做的事的命令）",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:117 / cstrike_english.txt:897 / cs_bot_radio.cpp:69-77" },

            // ---------- 组 C = radio3（C）----------
            new CsRadioLine { Id = 13, Set = CsRadioSet.Radio3, Slot = 1, EventName = "EVENT_RADIO_AFFIRMATIVE",
                Text = "Affirmative.",
                Trigger = "收到人类无线电命令且能执行 → 回执",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:119 / cstrike_english.txt:656 / cs_bot_radio.cpp:203-207" },
            new CsRadioLine { Id = 14, Set = CsRadioSet.Radio3, Slot = 2, EventName = "EVENT_RADIO_ENEMY_SPOTTED",
                Text = "Enemy spotted.",
                Trigger = "ReportEnemies()：存活 + 3s 内见过 ≥1 敌 + 本次未被压制 + 附近敌人归零前只报一次",
                Voice = "Sound/radio/ct_enemys",
                Source = "GameEvent.h:120 / cstrike_english.txt:730 / cs_bot_chatter.cpp:1391-1420,1625-1641 / cs_bot_vision.cpp:888" },
            new CsRadioLine { Id = 15, Set = CsRadioSet.Radio3, Slot = 3, EventName = "EVENT_RADIO_NEED_BACKUP",
                Text = "Need backup.",
                Trigger = "IsOutnumbered()（附近队友 < 附近敌人-1）为真时改发这条；10.0s 一次",
                Voice = "Sound/radio/ct_backup",
                Source = "GameEvent.h:121 / cstrike_english.txt:872 / cs_bot_chatter.cpp:1411,1796-1825 / cs_bot.cpp:659-662" },
            new CsRadioLine { Id = 16, Set = CsRadioSet.Radio3, Slot = 4, EventName = "EVENT_RADIO_SECTOR_CLEAR",
                Text = "Sector clear.",
                Trigger = "ReportingIn() 的『近期没见过敌人』分支（recentTime = 10.0f）",
                Voice = "Sound/radio/clear",
                Source = "GameEvent.h:122 / cstrike_english.txt:914 / cs_bot_chatter.cpp:1786-1790" },
            new CsRadioLine { Id = 17, Set = CsRadioSet.Radio3, Slot = 5, EventName = "EVENT_RADIO_IN_POSITION",
                Text = "I'm in position.",
                Trigger = "到达分工/守位点后报平安（对应原版按 GetTask() 上报的那一类）",
                Voice = "Sound/radio/ct_inpos",
                Source = "GameEvent.h:123 / cstrike_english.txt:841 / cs_bot_chatter.cpp:1709-1753" },
            new CsRadioLine { Id = 18, Set = CsRadioSet.Radio3, Slot = 6, EventName = "EVENT_RADIO_REPORTING_IN",
                Text = "Reporting in.",
                Trigger = "还有敌人 且无线电静默 > 30.0s 且 ShouldSpeak()（有存活队友、且并非所有队友都在身边）",
                Voice = "Sound/radio/ct_reportingin",
                Source = "GameEvent.h:124 / cstrike_english.txt:898 / cs_bot_chatter.cpp:1457-1464,1586-1597" },
            new CsRadioLine { Id = 19, Set = CsRadioSet.Radio3, Slot = 7, EventName = "EVENT_RADIO_GET_OUT_OF_THERE",
                Text = "Get out of there, it's gonna blow!",
                Trigger = "包已下 → EscapeFromBomb()（人类指挥）；bot 侧 = 离 C4 最近的那个 T 撤离时",
                Voice = "Sound/radio/getout",
                Source = "GameEvent.h:125 / cstrike_english.txt:776 / cs_bot_radio.cpp:165-174" },
            new CsRadioLine { Id = 20, Set = CsRadioSet.Radio3, Slot = 8, EventName = "EVENT_RADIO_NEGATIVE",
                Text = "Negative.",
                Trigger = "rogue bot 拒绝人类命令",
                Voice = CsRadioLine.NoVoice,
                Source = "GameEvent.h:126 / cstrike_english.txt:873 / cs_bot_radio.cpp:88-99" },
            new CsRadioLine { Id = 21, Set = CsRadioSet.Radio3, Slot = 9, EventName = "EVENT_RADIO_ENEMY_DOWN",
                Text = "Enemy down.",
                Trigger = "ReportingIn() 的『10s 内亲眼看到敌人死亡』分支",
                Voice = "Sound/radio/enemydown",
                Source = "GameEvent.h:127 / cstrike_english.txt:729 / cs_bot_chatter.cpp:1775-1780" },
        };

        /// <summary>全部 21 条（原版顺序）。</summary>
        public static IReadOnlyList<CsRadioLine> All => Table;

        /// <summary>消息总数（= 原版 21；离线完整性判据用它）。</summary>
        public static int Count => Table.Length;

        /// <summary>按本工程消息号取（<paramref name="id"/> = 1..21）。</summary>
        public static CsRadioLine ById(int id)
        {
            for (var i = 0; i < Table.Length; i++)
            {
                if (Table[i].Id == id) return Table[i];
            }
            return default;
        }

        /// <summary>该条消息的语音资源路径（相对 <c>Resources/</c>，不带扩展名）；
        /// 这一条没有解出的语音载体时返回 <c>null</c>。</summary>
        public static string VoiceOf(int id)
        {
            var line = ById(id);
            if (line.Id == 0 || string.IsNullOrEmpty(line.Voice) || line.Voice == CsRadioLine.NoVoice) return null;
            return line.Voice;
        }

        /// <summary>按原版枚举标识取（查不到返回 <c>default</c>，<c>Id == 0</c>）。</summary>
        public static CsRadioLine ByEventName(string eventName)
        {
            for (var i = 0; i < Table.Length; i++)
            {
                if (Table[i].EventName == eventName) return Table[i];
            }
            return default;
        }

        /// <summary>取某一组的全部条目（按槽位顺序）。</summary>
        public static List<CsRadioLine> Of(CsRadioSet set)
        {
            var list = new List<CsRadioLine>(9);
            for (var i = 0; i < Table.Length; i++)
            {
                if (Table[i].Set == set) list.Add(Table[i]);
            }
            return list;
        }

        /// <summary>组的原版本地化名。</summary>
        public static string SetLabel(CsRadioSet set)
        {
            switch (set)
            {
                case CsRadioSet.Radio1: return "Standard radio";   // cstrike_english.txt:9
                case CsRadioSet.Radio2: return "Group radio";       // cstrike_english.txt:10
                case CsRadioSet.Radio3: return "Report radio";      // cstrike_english.txt:11
                default: return "Radio";
            }
        }

        /// <summary>
        /// HUD 消息行 = 原版格式逐字：<c>"%s1 (RADIO): %s2"</c>
        /// （出处 <c>cstrike__resource__cstrike_english.txt:1748</c> 的 <c>Game_radio</c>）。
        /// </summary>
        public static string Format(string speakerName, CsRadioLine line)
        {
            return $"{speakerName} (RADIO): {line.Text}";
        }
    }
}
