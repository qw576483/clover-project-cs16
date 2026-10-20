using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 右上角击杀信息（规格 H8，策划案里叫 <c>KillFeedWidget</c>）：滚动显示最多
    /// <see cref="CsConst.MaxKillFeedEntries"/> 条 <c>杀者名 ‹武器图标› 被者名</c> —— **无文字动词**，
    /// 原版击杀条就是「名字 + 图标 + 名字」（`%s killed %s` 一类格式串在 `client.dll` / `mp.dll` 字节区
    /// 与全部在盘文本件里都不存在）。
    ///
    /// <para><b>图标 = 原版像素</b>：<c>sprites/hud.txt</c> 640 档的 <c>d_*</c>
    /// （<c>d_ak47 640 640hud1 192 80 48 16</c> …），逐件由 <c>tools/probes/spr-extract.py</c> 按该源矩形
    /// 从原版 <c>640hud1/2/16.spr</c> 解成 <c>Resources/UI/Art/killicon_d_*.png</c>；
    /// 名→武器见 <see cref="Cs16.Core.CsWeapons.DeathIconId"/>。
    /// 爆头标 <c>d_headshot</c>、自杀用 <c>d_skull</c>（原版 <c>CHudDeathNotice</c> 持有
    /// <c>m_HUD_d_headshot</c> / <c>m_HUD_d_skull</c>，<c>hlsdk/cl_dll/hud.h:409-422</c>）。
    /// 拿不到图标（含 <c>c4</c> —— hud.txt 无 <c>d_c4</c>）时该格回退文字 <c>[武器名]</c>。</para>
    ///
    /// <para>
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

        /// <summary>爆头标（原版 <c>d_headshot</c>，hud.txt 640 档源矩形 <c>0,240,36,16</c>）的原生像素尺寸。</summary>
        public const float HeadshotIconW = 36f;
        public const float HeadshotIconH = 16f;

        /// <summary>
        /// 一条击杀信息显示多久开始淡出（秒）。
        ///
        /// <para><b>为什么是 5.4</b>：原版 <c>hud_deathnotice_time</c> 默认 <b>6 秒</b>
        /// （出处 <c>原版资源/cs16src/cs16game/app/cstrike/cl_dlls/client.dll:0x0e77f8</c>）=
        /// "击杀条在屏幕上的总时长"。本件 = 停留 <see cref="HoldTime"/> + 淡出 <see cref="FadeTime"/>，
        /// 所以取 <c>6 − 0.6 = 5.4</c>，总时长正好 6 s（与 <see cref="CsHudTheme.MessageLifetime"/> 同源）。</para>
        /// </summary>
        public const float HoldTime = 5.4f;
        /// <summary>淡出时长（秒）。</summary>
        public const float FadeTime = 0.6f;

        public RectTransform Root;

        [System.NonSerialized] private readonly List<Row> _rows = new List<Row>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private readonly List<string> _keys = new List<string>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private readonly List<float> _seenAt = new List<float>(CsConst.MaxKillFeedEntries);
        [System.NonSerialized] private bool _warned;

        /// <summary>已取到的图标 sprite（键 = 原版 <c>d_*</c> 名）；取过一次就不再请求。</summary>
        [System.NonSerialized] private readonly Dictionary<string, Sprite> _icons = new Dictionary<string, Sprite>();
        /// <summary>正在异步取、还没回来的图标名（防止每帧重复发起）。</summary>
        [System.NonSerialized] private readonly HashSet<string> _iconPending = new HashSet<string>();
        [System.NonSerialized] private bool _iconWarned;

        private sealed class Row
        {
            public RectTransform Rt;
            public Text Killer;
            public Image Headshot;
            public Image Icon;
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

            // 起点在右上角比分 / 计时 / Map 行之下（见 CsHudTheme 的 Score* / Map* 常量），
            // 否则第一条击杀信息会压在比分行上。
            var root = UIFactory.CreateNode("KillFeed", parent);
            CsHudTheme.PlaceTopRight(root, new Vector2(-24f, -130f), new Vector2(RowWidth, RowHeight));
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
                var fallback = def != null ? $"[{def.DisplayName}]" : $"[{item.WeaponId ?? "?"}]";

                // 武器图标格：自杀（击杀者为空或 = 被击杀者）用原版 `d_skull`，其余按武器查 `d_*`
                // （`CsWeapons.DeathIconId`；`c4` 等无图标的返回 null）。图标没到手的那几帧先用文字占位。
                var suicide = string.IsNullOrEmpty(item.KillerName) || item.KillerName == item.VictimName;
                var dName = suicide ? "d_skull" : CsWeapons.DeathIconId(item.WeaponId);
                var icon = string.IsNullOrEmpty(dName) ? null : GetIcon(dName);
                if (icon != null)
                {
                    row.Icon.sprite = icon;
                    row.Icon.rectTransform.sizeDelta = new Vector2(icon.rect.width, icon.rect.height);
                    row.Icon.enabled = true;
                    row.Weapon.text = string.Empty;
                }
                else
                {
                    row.Icon.enabled = false;
                    row.Weapon.text = fallback;
                }

                // 爆头标 = 原版 `d_headshot`
                var hsSprite = item.Headshot ? GetIcon("d_headshot") : null;
                row.Headshot.sprite = hsSprite;
                row.Headshot.enabled = hsSprite != null;
            }
        }

        /// <summary>
        /// 取原版击杀条图标 sprite（缓存；没取到就发起异步请求，并且**只报一次** Warn）。
        /// 路径见 <see cref="ResPaths.KillIcon"/>；未到手时返回 null，调用方先用文字占位（⛔ 不静默）。
        /// </summary>
        private Sprite GetIcon(string dName)
        {
            if (_icons.TryGetValue(dName, out var cached)) return cached;
            if (_iconPending.Contains(dName)) return null;

            var res = Game.Res;
            if (res == null)
            {
                if (!_iconWarned)
                {
                    _iconWarned = true;
                    Game.Logger?.Warn("UI", "Game.Res 为 null（CloverRes.Init 未执行？），击杀条图标加载不了");
                }
                return null;
            }

            _iconPending.Add(dName);
            var key = ResPaths.KillIcon(dName);
            res.LoadAsset<Sprite>(key, sp =>
            {
                _iconPending.Remove(dName);
                if (sp == null)
                {
                    if (!_iconWarned)
                    {
                        _iconWarned = true;
                        Game.Logger?.Warn("UI",
                            $"击杀条图标加载失败（sprite 为空）：Resources/{key}（hud.txt 640 档 d_* 未解入工程？）");
                    }
                    return;
                }
                _icons[dName] = sp;
            });
            return null;
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

                // 爆头标 = 原版 `d_headshot`（CHudDeathNotice 的 m_HUD_d_headshot，hud.h:420）；sprite 到手才开。
                var hs = CsHudTheme.CreateBlock("Headshot", rt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(NameWidth + 2f, -((RowHeight - HeadshotIconH) * 0.5f)),
                    new Vector2(HeadshotIconW, HeadshotIconH), Color.white);
                hs.enabled = false;

                // 武器图标 = 原版 `d_*`（hud.txt 640 档源矩形，原生像素 1:1）；尺寸在 sprite 到手时按它设。
                var icon = CsHudTheme.CreateBlock("Icon", rt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(NameWidth + HeadshotIconW + 6f, -((RowHeight - HeadshotIconH) * 0.5f)),
                    new Vector2(WeaponWidth, HeadshotIconH), Color.white);
                icon.enabled = false;

                var weapon = CsHudTheme.CreateText("Weapon", rt, string.Empty, 18, TextAnchor.MiddleCenter,
                    CsHudTheme.TextDim);
                CsHudTheme.PlaceTopLeft(weapon.rectTransform, new Vector2(NameWidth + HeadshotIconW + 6f, 0f),
                    new Vector2(WeaponWidth, RowHeight));

                var victimX = NameWidth + HeadshotIconW + 6f + WeaponWidth;
                var victim = CsHudTheme.CreateText("Victim", rt, string.Empty, 19, TextAnchor.MiddleLeft,
                    CsHudTheme.TeamT);
                CsHudTheme.PlaceTopLeft(victim.rectTransform, new Vector2(victimX, 0f),
                    new Vector2(RowWidth - victimX, RowHeight));

                _rows.Add(new Row
                {
                    Rt = rt,
                    Killer = killer,
                    Headshot = hs,
                    Icon = icon,
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
