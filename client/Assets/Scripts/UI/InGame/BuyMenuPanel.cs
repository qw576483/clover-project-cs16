using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    ///
    /// <para><b>武器数据一律来自 <see cref="CsWeapons"/></b>（<c>BuyableByClass(cls, team)</c> +
    /// <c>CsWeaponDef.Price/DisplayName</c>）—— 界面上没有任何硬编码的价格或武器名，
    /// 改武器表就改表，UI 跟着变。</para>
    ///
    /// <para><b>分类口径</b>：按数字键的固定分组（1 手枪 / 2 冲锋 / 3 步枪 / 4 机枪 / 5 霰弹 / 6 装备 /
    /// 7 手雷 / 8 关闭）。其中"3 步枪"**并入狙击枪** —— CS 1.6 原版的 Rifles 页里就同时列着
    /// AK/M4 与 AWP/Scout/G3SG1/SG550，不并的话 AWP 这类标志性武器永远买不到。</para>
    ///
    /// <para><b>买不起 / 买不了</b>：买不起的条目变灰且不可点（规格 G3）；不在买枪区 / 不在买枪时间时，
    /// 顶部红字提示并且点击会被本地拦下（不发事件、给出原因），不让玩家点了没反应。</para>
    /// </summary>
    public class BuyMenuPanel : CsPanelBase
    {
        // ─────────── 布局 ───────────
        private const float DialogWidth = 1020f;
        private const float DialogHeight = 640f;
        private const float CategoryY = -104f;
        private const float CategoryWidth = 118f;
        private const float CategoryHeight = 46f;
        private const float ListTopY = -168f;
        private const float RowHeight = 46f;
        private const float RowGap = 6f;
        private const int RowsPerColumn = 6;
        private const float ColumnWidth = 470f;
        private const float ColumnGap = 24f;

        /// <summary>一个买枪分类（数字键 → 武器大类）。</summary>
        private struct Category
        {
            public string Key;
            public string Label;
            public CsWeaponClass[] Classes;

            public Category(string key, string label, params CsWeaponClass[] classes)
            {
                Key = key;
                Label = label;
                Classes = classes;
            }
        }

        private static readonly Category[] Categories =
        {
            new Category("1", "1 手枪", CsWeaponClass.Pistol),
            new Category("2", "2 冲锋", CsWeaponClass.SMG),
            // CS 1.6 的 Rifles 页含狙击枪：不并进来 AWP/Scout 就没有入口
            new Category("3", "3 步枪", CsWeaponClass.Rifle, CsWeaponClass.Sniper),
            new Category("4", "4 机枪", CsWeaponClass.MachineGun),
            new Category("5", "5 霰弹", CsWeaponClass.Shotgun),
            new Category("6", "6 装备", CsWeaponClass.Equipment),
            new Category("7", "7 手雷", CsWeaponClass.Grenade),
        };

        /// <summary>"8 关闭"在分类按钮数组里的下标（= 分类数）。</summary>
        private static readonly int CloseCategoryIndex = Categories.Length;

        // ─────────── 引用 ───────────
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _moneyText;
        [SerializeField] private Text _warnText;
        [SerializeField] private Text _bottomText;
        [SerializeField] private RectTransform _itemRoot;
        /// <summary>分类按钮（末尾多一个"8 关闭"，见 <see cref="CloseCategoryIndex"/>）。</summary>
        [SerializeField] private Button[] _categoryButtons;

        // ─────────── 运行期 ───────────
        private readonly List<CsWeaponDef> _items = new List<CsWeaponDef>(12);
        private readonly List<Row> _rows = new List<Row>(12);
        private int _category;
        private bool _warnedRefs;

        private sealed class Row
        {
            public Button Button;
            public Text Name;
            public Text Price;
            public Image Bg;
        }

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.5f), true);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Dialog", root, CsUiStyle.Box, true);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, new Vector2(DialogWidth, DialogHeight));
            var boxRt = box.rectTransform;

            _titleText = CsHudTheme.CreateText("Title", boxRt, "BUY MENU", 38, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_titleText.rectTransform, new Vector2(28f, -18f), new Vector2(500f, 48f));

            _moneyText = CsHudTheme.CreateText("Money", boxRt, "$0", 34, TextAnchor.MiddleRight,
                CsHudTheme.MoneyCanBuy);
            CsHudTheme.PlaceTopRight(_moneyText.rectTransform, new Vector2(-28f, -18f), new Vector2(340f, 48f));

            _warnText = CsHudTheme.CreateText("Warn", boxRt, string.Empty, 22, TextAnchor.MiddleLeft,
                CsHudTheme.Danger);
            CsHudTheme.PlaceTopLeft(_warnText.rectTransform, new Vector2(28f, -64f), new Vector2(900f, 30f));

            _categoryButtons = new Button[Categories.Length + 1];
            for (var i = 0; i < Categories.Length; i++)
            {
                var index = i;
                _categoryButtons[i] = CsHudTheme.CreateButton($"Cat{i}", boxRt, Categories[i].Label,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(28f + i * (CategoryWidth + 6f), CategoryY),
                    new Vector2(CategoryWidth, CategoryHeight), () => SelectCategory(index));
            }
            _categoryButtons[CloseCategoryIndex] = CsHudTheme.CreateButton("Cat_Close", boxRt, "8 关闭",
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(28f + CloseCategoryIndex * (CategoryWidth + 6f), CategoryY),
                new Vector2(CategoryWidth, CategoryHeight), Close);

            var items = UIFactory.CreateNode("Items", boxRt);
            CsHudTheme.PlaceTopLeft(items, new Vector2(24f, ListTopY),
                new Vector2(DialogWidth - 48f, RowHeight * RowsPerColumn + RowGap * (RowsPerColumn - 1)));
            _itemRoot = items;

            _bottomText = CsHudTheme.CreateText("Bottom", boxRt, string.Empty, 20, TextAnchor.MiddleLeft,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceBottomLeft(_bottomText.rectTransform, new Vector2(28f, 16f),
                new Vector2(DialogWidth - 56f, 30f));
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_itemRoot, "武器列表容器");
            WarnIfNull(_titleText, "标题文本");
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_categoryButtons == null || _categoryButtons.Length != Categories.Length + 1)
                    Game.Logger?.Error(Tag, "BuyMenuPanel 分类按钮引用不完整（预制体未由 UiBuilder 生成？），数字键仍然可用");
            }

            _category = 0;
            ForEachCategoryButton(BindCategoryButton);

            // 买枪菜单要点击 → 光标必须解锁（操作模块锁定光标时 uGUI 只能命中屏幕正中）
            CsIngameCursor.UnlockForMenu(nameof(BuyMenuPanel));

            RebuildItems();
            Refresh();

            Game.Logger?.Info(Tag,
                $"买枪菜单已打开：阵营={CsHudSnapshot.Team} 金钱=${CsHudSnapshot.Money} " +
                $"在买枪区={CsHudSnapshot.InBuyZone} 可买={CsHudSnapshot.CanBuyNow}");
        }

        public override void OnClose()
        {
            CsIngameCursor.RelockIfIdle(nameof(BuyMenuPanel), CsIngameCursor.AnyMenuOpen());
        }

        public override void OnUpdate(float dt)
        {
            var input = Game.Input;
            if (input != null)
            {
                // 1~8：切分类 / 关闭（规格 G3 的数字键）
                for (var i = 0; i < Categories.Length; i++)
                {
                    if (input.GetKeyDown(CategoryKey(i))) SelectCategory(i);
                }
                if (input.GetKeyDown(CategoryKey(CloseCategoryIndex)) || input.GetKeyDown(GameKey.B) ||
                    input.GetKeyDown(GameKey.Escape))
                {
                    Close();
                    return;
                }
            }

            Refresh();
        }

        private static GameKey CategoryKey(int index)
        {
            switch (index)
            {
                case 0: return GameKey.Num1;
                case 1: return GameKey.Num2;
                case 2: return GameKey.Num3;
                case 3: return GameKey.Num4;
                case 4: return GameKey.Num5;
                case 5: return GameKey.Num6;
                case 6: return GameKey.Num7;
                default: return GameKey.Num8;
            }
        }

        private void BindCategoryButton(int index, Button button)
        {
            // 末尾那个是"8 关闭"（不是分类）—— 按钮的 onClick 是运行期监听器，预制体里存不下来，
            // 每次 OnOpen 必须重新绑（这是本工程所有面板的通用约定，见 CsPanelBase.Bind 的注释）
            if (index >= Categories.Length)
            {
                Bind(button, Close, "8 关闭");
                return;
            }

            var i = index;
            Bind(button, () => SelectCategory(i), $"分类 {Categories[index].Key}");
        }

        private void ForEachCategoryButton(System.Action<int, Button> action)
        {
            if (_categoryButtons == null) return;
            for (var i = 0; i < _categoryButtons.Length; i++) action(i, _categoryButtons[i]);
        }

        private void Close()
        {
            Game.Logger?.Info(Tag, "买枪菜单关闭");
            Game.UI.Close<BuyMenuPanel>();
        }

        // ═══════════════════════ 分类 / 列表 ═══════════════════════

        private void SelectCategory(int index)
        {
            if (index < 0 || index >= Categories.Length)
            {
                Game.Logger?.Warn(Tag, $"买枪分类序号 {index} 越界（共 {Categories.Length} 类），已忽略");
                return;
            }

            _category = index;
            Game.Logger?.Info(Tag, $"买枪分类切换：{Categories[index].Label}");
            RebuildItems();
            Refresh();
        }

        private void RebuildItems()
        {
            _items.Clear();

            var team = CsHudSnapshot.Team;
            var category = Categories[Mathf.Clamp(_category, 0, Categories.Length - 1)];
            for (var i = 0; i < category.Classes.Length; i++)
            {
                var list = CsWeapons.BuyableByClass(category.Classes[i], team);
                for (var j = 0; j < list.Count; j++) _items.Add(list[j]);
            }

            EnsureRows(_items.Count);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;

                if (i >= _items.Count)
                {
                    if (row.Button != null && row.Button.gameObject.activeSelf)
                        row.Button.gameObject.SetActive(false);
                    continue;
                }

                var def = _items[i];
                if (row.Button != null && !row.Button.gameObject.activeSelf)
                    row.Button.gameObject.SetActive(true);
                if (row.Name != null) row.Name.text = def.DisplayName;
                if (row.Price != null) row.Price.text = def.Price > 0 ? $"${def.Price}" : "-";

                var id = def.Id;
                Bind(row.Button, () => TryBuy(id), $"购买 {def.DisplayName}");
            }

            // 分类列表为空 = 数据/阵营组合有问题，不能静默留一片空白
            if (_items.Count == 0)
                Game.Logger?.Warn(Tag, $"买枪分类「{category.Label}」在当前阵营（{team}）下没有任何可买武器");
        }

        private void EnsureRows(int count)
        {
            while (_rows.Count < count)
            {
                _rows.Add(CreateRow(_rows.Count));
            }
        }

        private Row CreateRow(int index)
        {
            var column = index / RowsPerColumn;
            var line = index % RowsPerColumn;
            var size = new Vector2(ColumnWidth, RowHeight);
            var pos = new Vector2(column * (ColumnWidth + ColumnGap), -line * (RowHeight + RowGap));

            var bg = UIFactory.CreatePanel($"Item{index}", _itemRoot, CsUiStyle.Field, true);
            CsHudTheme.PlaceTopLeft(bg.rectTransform, pos, size);
            var button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;

            var colors = button.colors;
            colors.normalColor = CsUiStyle.Field;
            colors.highlightedColor = CsUiStyle.AccentDim;
            colors.pressedColor = CsUiStyle.Accent;
            colors.selectedColor = CsUiStyle.Field;
            colors.disabledColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.05f;
            button.colors = colors;

            var name = CsHudTheme.CreateText("Name", bg.rectTransform, "-", 22, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(name.rectTransform, new Vector2(14f, 0f),
                new Vector2(ColumnWidth - 130f, RowHeight));
            name.raycastTarget = false;

            var price = CsHudTheme.CreateText("Price", bg.rectTransform, "-", 22, TextAnchor.MiddleRight,
                CsHudTheme.MoneyNormal);
            CsHudTheme.PlaceTopRight(price.rectTransform, new Vector2(-14f, 0f), new Vector2(120f, RowHeight));
            price.raycastTarget = false;

            return new Row { Button = button, Name = name, Price = price, Bg = bg };
        }

        // ═══════════════════════ 刷新 / 购买 ═══════════════════════

        private void Refresh()
        {
            var money = CsHudSnapshot.Money;
            var canBuyNow = CsHudSnapshot.CanBuyNow;
            var inZone = CsHudSnapshot.InBuyZone;

            if (_moneyText != null)
            {
                _moneyText.text = $"${money}";
                _moneyText.color = canBuyNow ? CsHudTheme.MoneyCanBuy : CsHudTheme.MoneyNormal;
            }

            if (_warnText != null)
            {
                if (!inZone) _warnText.text = "必须在买枪区内（Buy Zone）才能购买";
                else if (!canBuyNow) _warnText.text = "现在不是买枪时间（只能在每回合开局的买枪时间内购买）";
                else if (CsHudSnapshot.Team != CsTeam.T && CsHudSnapshot.Team != CsTeam.CT)
                    _warnText.text = "观察者不能购买武器";
                else _warnText.text = string.Empty;
            }

            if (_bottomText != null)
            {
                var weapon = string.IsNullOrEmpty(CsHudSnapshot.WeaponName) ? "无" : CsHudSnapshot.WeaponName;
                _bottomText.text =
                    $"当前持有：{weapon}（{CsHudTheme.AmmoText(CsHudSnapshot.Mag, CsHudSnapshot.Reserve)}）　" +
                    "1~7 切分类，8 / B / ESC 关闭";
            }

            // 买不起的条目变灰且不可点（规格 G3）
            for (var i = 0; i < _rows.Count && i < _items.Count; i++)
            {
                var row = _rows[i];
                if (row?.Button == null) continue;

                var affordable = money >= _items[i].Price;
                if (row.Button.interactable != affordable) row.Button.interactable = affordable;
                if (row.Price != null)
                    row.Price.color = affordable ? CsHudTheme.MoneyNormal : CsHudTheme.Disabled;
                if (row.Name != null)
                    row.Name.color = affordable ? CsHudTheme.TextMain : CsHudTheme.Disabled;
            }

            RefreshCategoryHighlight();
        }

        private void RefreshCategoryHighlight()
        {
            if (_categoryButtons == null) return;
            for (var i = 0; i < _categoryButtons.Length && i < Categories.Length; i++)
            {
                var button = _categoryButtons[i];
                if (button == null) continue;

                var selected = i == _category;
                var colors = button.colors;
                colors.normalColor = selected ? CsUiStyle.AccentDim : CsUiStyle.Field;
                colors.selectedColor = colors.normalColor;
                colors.highlightedColor = CsUiStyle.Accent;
                colors.pressedColor = CsUiStyle.AccentDim;
                colors.colorMultiplier = 1f;
                button.colors = colors;
            }
        }

        private void TryBuy(string weaponId)
        {
            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                Game.Logger?.Error(Tag, $"买枪菜单点了一个不存在的武器 id：{weaponId}（武器表 CsWeapons 里没有）");
                HudPanel.Notify($"未知武器：{weaponId}");
                return;
            }

            // 先在本地把"必然失败"的情况挡住：比赛模块对这些失败只打 Warn 日志、不推消息栏事件，
            // 不挡的话玩家点下去会毫无反馈（点了没反应是最糟的交互）。
            if (!CsHudSnapshot.InBuyZone)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：不在买枪区");
                HudPanel.Notify("不能购买：必须在买枪区内");
                return;
            }
            if (!CsHudSnapshot.CanBuyNow)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：不在买枪时间");
                HudPanel.Notify("不能购买：不在买枪时间内");
                return;
            }
            if (CsHudSnapshot.Money < def.Price)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：金钱不足（${CsHudSnapshot.Money} < ${def.Price}）");
                HudPanel.Notify($"金钱不足：{def.DisplayName} 需要 ${def.Price}");
                return;
            }

            Game.Logger?.Info(Tag, $"买枪请求：{def.DisplayName}（{def.Id}）${def.Price}，由比赛模块判定是否成交");
            Game.Event.Emit(Events.BuyWeapon, def.Id);
            HudPanel.Notify($"购买 {def.DisplayName}（${def.Price}）");
        }
    }
}
