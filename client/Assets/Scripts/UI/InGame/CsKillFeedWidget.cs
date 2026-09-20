using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 右上角击杀信息（规格 H8，策划案里叫 <c>KillFeedWidget</c>）：滚动显示最多
    /// <see cref="CsConst.MaxKillFeedEntries"/> 条 <c>A [AK-47] B</c>，爆头带一个红点图标。
    ///
    /// <para>
    /// 数据源是 <see cref="CsHudSnapshot.KillFeed"/>（agent-03 每帧重填，条目带
    /// <see cref="CsKillFeedItem.BornTime"/>）。本件**不自己记时间**（不订阅事件、不看 <c>Time.time</c> 与
    /// BornTime 的差）—— 因为 BornTime 用的是比赛模拟的时钟（<c>CsMatch.Clock</c>，默认 <c>Time.time</c>，
    /// 自动化测试里会被换成假时钟）。改成本地"第一次看到该条目的时刻"计时，
    /// 这样无论上层用什么时钟，淡出时长都是对的。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsKillFeedWidget
    {
        public const float RowHeight = 24f;
        public const float RowWidth = 480f;
        public const float NameWidth = 168f;
        public const float WeaponWidth = 120f;
        public const float HeadshotIcon = 10f;

        /// <summary>
        /// 一条击杀信息显示多久开始淡出（秒）。
        ///
        /// <para><b>为什么是 5.4</b>：原版 <c>hud_deathnotice_time</c> 默认 <b>6 秒</b>
        /// （出处 <c>原版资源/cs16src/cs16game/app/cstrike/cl_dlls/client.dll:0x0e77f8</c>）=
        /// "击杀条在屏幕上的总时长"。本件 = 停留 <see cref="HoldTime"/> + 淡出 <see cref="FadeTime"/>，
        /// 所以取 <c>6 − 0.6 = 5.4</c>，总时长正好 6 s（与 <see cref="CsHudTheme.MessageLifetime"/> 同源）。
        /// ⛔ 旧值 <c>4.5</c>（总 5.1 s）没有出处（对照表 U-36：差 −2 s 的口径按"系统消息行"记）。</para>
        /// </summary>
        public const float HoldTime = 5.4f;
        /// <summary>淡出时长（秒）。</summary>
        public const float FadeTime = 0.6f;

        public RectTransform Root;

        [System.NonSerialized] private readonly List<Row> _rows = new List<Row>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private readonly List<string> _keys = new List<string>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private readonly List<float> _seenAt = new List<float>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private bool _warned;

        private sealed class Row
        {
            public RectTransform Rt;
            public Text Killer;
            public Image Headshot;
            public Text Weapon;
            public Text Victim;
            public CanvasGroup Group;
        }

        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsKillFeedWidget.Build 收到 null 父节点，击杀信息不会显示");
                return;
            }

            var root = UIFactory.CreateNode("KillFeed", parent);
            CsHudTheme.PlaceTopRight(root, new Vector2(-24f, -14f), new Vector2(RowWidth, RowHeight));
            Root = root;
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">比赛没跑时隐藏。</param>
        /// <param name="feed"><see cref="CsHudSnapshot.KillFeed"/>（最新的在前）。</param>
        public void Refresh(bool visible, List<CsKillFeedItem> feed)
        {
            if (Root == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Game.Logger?.Error("UI", "CsKillFeedWidget 引用缺失（预制体未由 UiBuilder 生成或已被改动），击杀信息不会显示");
                }
                return;
            }

            if (Root.gameObject.activeSelf != visible) Root.gameObject.SetActive(visible);
            if (!visible) return;

            if (feed == null)
            {
                Game.Logger?.Warn("UI", "CsKillFeedWidget 收到 null 的击杀列表，本帧不刷");
                return;
            }

            var count = Mathf.Min(feed.Count, CsConst.MaxKillFeedEntries);
            EnsureRows(count);

            var now = Time.time;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;

                if (i >= count)
                {
                    if (row.Rt.gameObject.activeSelf) row.Rt.gameObject.SetActive(false);
                    continue;
                }

                var item = feed[i];
                var key = $"{item.BornTime:0.000}|{item.KillerName}|{item.VictimName}|{item.WeaponId}|{item.Headshot}";

                // key 变化 = 这一行换了一条击杀信息 → 重置它的本地计时（不是重置整屏）
                if (_keys[i] != key)
                {
                    _keys[i] = key;
                    _seenAt[i] = now;
                }

                var age = now - _seenAt[i];
                float alpha;
                if (age <= HoldTime) alpha = 1f;
                else alpha = Mathf.Clamp01(1f - (age - HoldTime) / FadeTime);

                if (!row.Rt.gameObject.activeSelf) row.Rt.gameObject.SetActive(true);
                row.Group.alpha = alpha;
                row.Killer.text = string.IsNullOrEmpty(item.KillerName) ? "?" : item.KillerName;
                row.Killer.color = item.Headshot ? CsHudTheme.TextMain : CsHudTheme.TeamColor(item.KillerTeam);
                row.Victim.text = string.IsNullOrEmpty(item.VictimName) ? "?" : item.VictimName;
                row.Victim.color = CsHudTheme.TeamColor(item.VictimTeam);

                var def = CsWeapons.Get(item.WeaponId);
                row.Weapon.text = def != null ? $"[{def.DisplayName}]" : $"[{item.WeaponId ?? "?"}]";
                row.Headshot.enabled = item.Headshot;
            }
        }

        private void EnsureRows(int count)
        {
            while (_rows.Count < count)
            {
                var index = _rows.Count;
                var rt = UIFactory.CreateNode($"Feed{index}", Root);
                CsHudTheme.PlaceTopRight(rt, new Vector2(0f, -index * RowHeight), new Vector2(RowWidth, RowHeight));
                var group = rt.gameObject.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                group.interactable = false;

                var killer = CsHudTheme.CreateText("Killer", rt, string.Empty, 19, TextAnchor.MiddleRight,
                    CsHudTheme.TeamCT);
                CsHudTheme.PlaceTopLeft(killer.rectTransform, new Vector2(0f, 0f), new Vector2(NameWidth, RowHeight));

                var hs = CsHudTheme.CreateBlock("Headshot", rt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(NameWidth + 4f, -((RowHeight - HeadshotIcon) * 0.5f)),
                    new Vector2(HeadshotIcon, HeadshotIcon), CsHudTheme.Danger);
                hs.enabled = false;

                var weapon = CsHudTheme.CreateText("Weapon", rt, string.Empty, 18, TextAnchor.MiddleCenter,
                    CsHudTheme.TextDim);
                CsHudTheme.PlaceTopLeft(weapon.rectTransform, new Vector2(NameWidth + HeadshotIcon + 8f, 0f),
                    new Vector2(WeaponWidth, RowHeight));

                var victimX = NameWidth + HeadshotIcon + 8f + WeaponWidth;
                var victim = CsHudTheme.CreateText("Victim", rt, string.Empty, 19, TextAnchor.MiddleLeft,
                    CsHudTheme.TeamT);
                CsHudTheme.PlaceTopLeft(victim.rectTransform, new Vector2(victimX, 0f),
                    new Vector2(RowWidth - victimX, RowHeight));

                _rows.Add(new Row
                {
                    Rt = rt,
                    Killer = killer,
                    Headshot = hs,
                    Weapon = weapon,
                    Victim = victim,
                    Group = group,
                });
                _keys.Add(string.Empty);
                _seenAt.Add(0f);
            }
        }
    }
}
