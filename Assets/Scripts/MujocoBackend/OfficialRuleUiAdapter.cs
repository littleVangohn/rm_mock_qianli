using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Pacific.Mujoco
{
    // UI input only. The original RefereeController/GameManager still validates
    // area, stock, coins and remaining remote-purchase uses on the server.
    public sealed class OfficialRuleUiAdapter : MonoBehaviour
    {
        private OfficialLayout layout;
        private int ammo0Amount;
        private int ammo1Amount;
        private int exchangeLevel;
        private int exchangeRows;
        private PurchaseType? pendingRemoteType;
        private int pendingRemoteFaction;
        private int pendingRemoteUses;
        private int pendingRemoteCoins;
        private float pendingRemoteAt;
        private Text remoteStatus;

        public void Initialize(OfficialLayout officialLayout)
        {
            layout = officialLayout;
            WireAmmoPanel("SupplyItem2024", false);
            WireAmmoPanel("HeroSupplyItem2024", true);
            WireRemotePurchase();
            WireEngineerExchange();
        }

        private RefereeController SelectedRobot()
        {
            if (!PacificSelectionState.Completed) return null;
            int id = PacificSelectionState.SelectedActualRobotId;
            foreach (RefereeController referee in FindObjectsByType<RefereeController>(FindObjectsSortMode.None))
            {
                NetworkObject networkObject = referee.GetComponent<NetworkObject>();
                if (networkObject != null && networkObject.IsSpawned && referee.RobotID.Value == id)
                    return referee;
            }
            return null;
        }

        private static int Safe(Unity.Netcode.NetworkList<int> values, int index)
            => values != null && index >= 0 && index < values.Count ? values[index] : 0;

        private static Button WireButton(Transform target, Action action)
        {
            if (target == null) return null;
            Image graphic = target.GetComponent<Image>();
            if (graphic == null)
            {
                graphic = target.gameObject.AddComponent<Image>();
                graphic.color = Color.clear;
            }
            graphic.raycastTarget = true;
            Button button = target.GetComponent<Button>();
            if (button == null) button = target.gameObject.AddComponent<Button>();
            button.targetGraphic = graphic;
            button.onClick.AddListener(() => action());
            return button;
        }

        private void WireAmmoPanel(string path, bool large)
        {
            RectTransform panel = layout.Find(path);
            if (panel == null) return;
            int[] steps = large ? new[] { 1, 2, 5, 10 } : new[] { 10, 20, 50, 100 };
            foreach (int step in steps)
            {
                int capturedStep = step;
                WireButton(panel.Find("SupplyButton/Add_" + step), () => AdjustAmmo(large, capturedStep));
                WireButton(panel.Find("SupplyButton/Minus_" + step), () => AdjustAmmo(large, -capturedStep));
            }
            WireButton(panel.Find("Confirm"), () => ConfirmAmmo(panel, large));
            WireButton(panel.Find("Popup/NO"), () => ShowAmmoPopup(panel, false));
            WireButton(panel.Find("Popup/OK"), () => BuyAmmo(panel, large));
            RefreshAmmo(panel, large);
        }

        private void AdjustAmmo(bool large, int delta)
        {
            GameManager manager = FindFirstObjectByType<GameManager>();
            RefereeController robot = SelectedRobot();
            int stock = manager != null && robot != null
                ? Safe(large ? manager.Ammo1Supply : manager.Ammo0Supply, (int)robot.faction.Value) : 0;
            if (large) ammo1Amount = Mathf.Clamp(ammo1Amount + delta, 0, stock);
            else ammo0Amount = Mathf.Clamp(ammo0Amount + delta, 0, stock);
            RefreshAmmo(layout.Find(large ? "HeroSupplyItem2024" : "SupplyItem2024"), large);
        }

        private void RefreshAmmo(RectTransform panel, bool large)
        {
            if (panel == null) return;
            RefereeController robot = SelectedRobot();
            GameManager manager = FindFirstObjectByType<GameManager>();
            Text number = panel.Find("SupplyButton/ShowNum")?.GetComponent<Text>();
            Text available = panel.Find("Middle/AvailableBullets")?.GetComponent<Text>();
            if (number != null) number.text = (large ? ammo1Amount : ammo0Amount).ToString();
            if (available != null)
            {
                int stock = manager != null && robot != null
                    ? Safe(large ? manager.Ammo1Supply : manager.Ammo0Supply, (int)robot.faction.Value) : 0;
                int owned = robot != null ? (large ? robot.Ammo1.Value : robot.Ammo0.Value) : 0;
                available.text = owned + "/" + stock;
            }
        }

        private static void ShowAmmoPopup(RectTransform panel, bool visible)
        {
            Transform popup = panel != null ? panel.Find("Popup") : null;
            if (popup != null) popup.gameObject.SetActive(visible);
        }

        private void ConfirmAmmo(RectTransform panel, bool large)
        {
            RefereeController robot = SelectedRobot();
            int amount = large ? ammo1Amount : ammo0Amount;
            if (robot == null || amount <= 0 || !robot.InSupplyArea.Value ||
                (large ? !robot.has42mmShooter() : !robot.has17mmShooter()))
            {
                ShowError(panel, "未处于补给区、无对应发射机构或补弹数量为零");
                return;
            }
            GameManager manager = FindFirstObjectByType<GameManager>();
            int cost = amount * (large ? 15 : 1);
            int stock = manager != null ? Safe(large ? manager.Ammo1Supply : manager.Ammo0Supply,
                (int)robot.faction.Value) : 0;
            if (amount > stock)
            {
                ShowError(panel, $"队伍弹药库存不足：剩余 {stock}");
                return;
            }
            if (manager == null || Safe(manager.Coins, (int)robot.faction.Value) < cost)
            {
                ShowError(panel, "金币不足");
                return;
            }
            Text tip = panel.Find("Popup/CostTip")?.GetComponent<Text>();
            if (tip != null) tip.text = "本次兑换将消耗 " + cost + " 金币";
            ShowAmmoPopup(panel, true);
        }

        private void BuyAmmo(RectTransform panel, bool large)
        {
            RefereeController robot = SelectedRobot();
            int amount = large ? ammo1Amount : ammo0Amount;
            ShowAmmoPopup(panel, false);
            GameManager manager = FindFirstObjectByType<GameManager>();
            if (robot == null || manager == null || amount <= 0 || !robot.InSupplyArea.Value ||
                (large ? !robot.has42mmShooter() : !robot.has17mmShooter()))
            { ShowError(panel, "车辆不在补给区或无法使用该弹药"); return; }
            int faction = (int)robot.faction.Value;
            int stock = Safe(large ? manager.Ammo1Supply : manager.Ammo0Supply, faction);
            if (amount > stock || Safe(manager.Coins, faction) < amount * (large ? 15 : 1))
            { ShowError(panel, "队伍库存或金币已变化，请重新确认"); return; }
            robot.Purchase(large ? PurchaseType.Ammo1 : PurchaseType.Ammo0, amount);
            if (large) ammo1Amount = 0; else ammo0Amount = 0;
            RefreshAmmo(panel, large);
        }

        private static void ShowError(RectTransform panel, string message)
        {
            Transform border = panel != null ? panel.Find("ErrorBorder") : null;
            if (border == null) return;
            Text text = border.Find("message")?.GetComponent<Text>();
            if (text != null) text.text = message;
            border.gameObject.SetActive(true);
        }

        private void WireRemotePurchase()
        {
            RectTransform panel = layout.Find("RemoteExchange");
            if (panel == null) return;
            // Tab1 is the collapsed "press H" badge; Tab2 is the open item list. The
            // official controller swaps them, but here the root itself is the open/close
            // state, so leaving Tab1 active would draw the H badge over the item list.
            Transform hint = panel.Find("Tab1");
            if (hint != null) hint.gameObject.SetActive(false);
            // The generic popup wash stretches to the root's 100x100 placeholder,
            // painting a bright square over the middle column instead of the board.
            Transform washTransform = panel.Find("Popup Light Wash");
            RectTransform wash = washTransform as RectTransform;
            if (wash != null)
            {
                wash.anchorMin = wash.anchorMax = new Vector2(0.5f, 0.5f);
                wash.pivot = new Vector2(0.5f, 0.5f);
                wash.sizeDelta = new Vector2(330f, 190f);
                wash.anchoredPosition = new Vector2(0f, -43f);
            }
            // The extracted panel displays CTRL+1/2/3 but has no original
            // controller attached. Its three item roots are full-board 330x190
            // siblings stacked on the same spot, so wiring them directly would make
            // the topmost one (Item1) swallow every click. Bind one hit area per
            // visible column instead, since RefereeController.RemotePurchase backs it.
            Transform tab = panel.Find("Tab2");
            // The official captions are set in BlenderPro; the same 28px in
            // Microsoft YaHei is ~25% wider and the three centred captions would
            // collide. Shrink them to the official ~96px column pitch.
            for (int i = 1; i <= 3; i++)
            {
                Text caption = tab?.Find("Item" + i + "/label")?.GetComponent<Text>();
                if (caption != null) caption.fontSize = 18;

                // The prefab's 106px keycap box wraps "CTRL + 1" and the vertical
                // truncate hides the digit. Overflow keeps the hint on one line; at
                // the node's 0.5 scale it still sits inside the 64px Button-Tips
                // background.
                Text keyHint = tab?.Find("Item" + i + "/buttonkeybg/buttonkey")?.GetComponent<Text>();
                if (keyHint != null)
                {
                    keyHint.horizontalOverflow = HorizontalWrapMode.Overflow;
                    keyHint.verticalOverflow = VerticalWrapMode.Overflow;
                }
            }
            WireRemoteHit(tab, "Item1 Hit", -106f, () => RemotePurchase(PurchaseType.Remote_HP));
            WireRemoteHit(tab, "Item2 Hit", -4f, () => RemotePurchase(PurchaseType.Remote_Ammo0));
            WireRemoteHit(tab, "Item3 Hit", 102f, () => RemotePurchase(PurchaseType.Remote_Ammo1));
            GameObject status = new GameObject("Source Purchase Status", typeof(RectTransform), typeof(Text));
            status.transform.SetParent(panel, false);
            RectTransform rect = status.GetComponent<RectTransform>();
            // Sits in the 18px gap between the CTRL keycaps and the footer bar. Anchoring
            // to the 100x100 panel rect made this line 64x7 and clipped every message.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(330f, 18f);
            rect.anchoredPosition = new Vector2(0f, -75f);
            remoteStatus = status.GetComponent<Text>();
            remoteStatus.font = MujocoBranding.UiFont();
            remoteStatus.fontSize = 14;
            remoteStatus.alignment = TextAnchor.MiddleCenter;
            remoteStatus.horizontalOverflow = HorizontalWrapMode.Overflow;
            remoteStatus.verticalOverflow = VerticalWrapMode.Overflow;
            remoteStatus.color = Color.white;
            remoteStatus.raycastTarget = false;
            remoteStatus.text = "选择补给项目；最终结果以裁判状态为准";
        }

        private static void WireRemoteHit(Transform tab, string name, float centerX, Action action)
        {
            if (tab == null) return;
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(tab, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            // Covers the icon/label/keycap cluster but stops above the footer bar.
            rect.sizeDelta = new Vector2(100f, 120f);
            rect.anchoredPosition = new Vector2(centerX, -10f);
            Image image = go.GetComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = true;
            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => action());
        }

        private void RemotePurchase(PurchaseType type)
        {
            if (pendingRemoteType.HasValue)
            { SetRemoteStatus("上一笔购买请求正在等待裁判确认"); return; }
            RefereeController robot = SelectedRobot();
            GameManager manager = FindFirstObjectByType<GameManager>();
            if (robot == null || manager == null || !robot.Enabled.Value)
            { SetRemoteStatus("车辆或裁判未就绪"); return; }
            if (!robot.Disengaged.Value)
            { SetRemoteStatus("交战中不能远程补给"); return; }
            if (type == PurchaseType.Remote_HP && !robot.robotTags.Contains(RobotTag.GroundUnit) ||
                type == PurchaseType.Remote_Ammo0 && !robot.has17mmShooter() ||
                type == PurchaseType.Remote_Ammo1 && !robot.has42mmShooter())
            { SetRemoteStatus("当前车型没有对应的补给能力"); return; }
            int faction = (int)robot.faction.Value;
            int uses = RemoteUses(manager, type, faction);
            // The original referee uses -1 for unlimited purchases; only
            // exactly zero means the team's quota is exhausted.
            if (uses == 0)
            { SetRemoteStatus("本队此项远程补给次数已用完"); return; }
            int cost = RemoteCost(manager, type);
            int coins = Safe(manager.Coins, faction);
            if (coins < cost)
            { SetRemoteStatus($"金币不足：需要 {cost}，当前 {coins}"); return; }
            pendingRemoteType = type;
            pendingRemoteFaction = faction;
            pendingRemoteUses = uses;
            pendingRemoteCoins = coins;
            pendingRemoteAt = Time.unscaledTime;
            SetRemoteStatus($"已提交购买请求（{cost} 金币），等待裁判确认");
            robot.RemotePurchase(type);
        }

        private static int RemoteUses(GameManager manager, PurchaseType type, int faction)
        {
            switch (type)
            {
                case PurchaseType.Remote_HP: return Safe(manager.RemoteHPTimes, faction);
                case PurchaseType.Remote_Ammo0: return Safe(manager.RemoteAmmo0Times, faction);
                case PurchaseType.Remote_Ammo1: return Safe(manager.RemoteAmmo1Times, faction);
                default: return 0;
            }
        }

        private static int RemoteCost(GameManager manager, PurchaseType type)
        {
            bool rmuc2024 = manager is RMUC2024_GameManager;
            switch (type)
            {
                case PurchaseType.Remote_HP:
                    return (rmuc2024 ? 50 : 100) + Mathf.CeilToInt((420f - manager.TimeLeft.Value) / 60f) * 20;
                case PurchaseType.Remote_Ammo0: return rmuc2024 ? 150 : 200;
                case PurchaseType.Remote_Ammo1: return rmuc2024 ? 200 : 300;
                default: return 0;
            }
        }

        private void SetRemoteStatus(string message)
        {
            if (remoteStatus != null) remoteStatus.text = message;
        }

        private void ReconcileRemotePurchase()
        {
            if (!pendingRemoteType.HasValue) return;
            GameManager manager = FindFirstObjectByType<GameManager>();
            if (manager == null) return;
            if (RemoteUses(manager, pendingRemoteType.Value, pendingRemoteFaction) < pendingRemoteUses ||
                Safe(manager.Coins, pendingRemoteFaction) < pendingRemoteCoins)
            {
                SetRemoteStatus("裁判已确认购买，补给将按原规则到账");
                pendingRemoteType = null;
            }
            else if (Time.unscaledTime - pendingRemoteAt > 2f)
            {
                SetRemoteStatus("裁判未确认购买；请检查交战状态与金币");
                pendingRemoteType = null;
            }
        }

        private void WireEngineerExchange()
        {
            RectTransform panel = layout.Find("ExchangeItem2024");
            if (panel == null) return;
            WireButton(panel.Find("Difficulty/Button"), ConfirmEngineerExchange);
            WireButton(panel.Find("p2/Cancel"), () => SetEngineerExchange(false));
            WireButton(panel.Find("p2/Confirrm"), () => SetEngineerExchange(true));
        }

        private void ConfirmEngineerExchange() => SetEngineerExchange(true);

        private void SetEngineerExchange(bool enabled)
        {
            RefereeController robot = SelectedRobot();
            GameManager manager = FindFirstObjectByType<GameManager>();
            RectTransform panel = layout.Find("ExchangeItem2024");
            bool supported = robot != null && robot.robotClass.Value == RobotClass.Engineer &&
                manager != null && (robot.faction.Value == Faction.Red
                    ? manager.RedExchangeStation != null : manager.BlueExchangeStation != null);
            if (!supported || (enabled && (exchangeLevel < 0 || exchangeLevel >= robot.ExchangeSpeed.Count)))
            {
                Text tip = panel?.Find("Difficulty/tip2")?.GetComponent<Text>();
                if (tip != null) tip.text = "当前车型或模式不支持兑换等级设置";
                return;
            }
            robot.ChooseExchangeLevel(enabled, exchangeLevel);
            Text status = panel?.Find("Difficulty/tip2")?.GetComponent<Text>();
            if (status != null) status.text = enabled ? $"已请求 {exchangeLevel + 1} 级兑换" : "已取消兑换";
        }

        private void RefreshEngineerExchange()
        {
            RectTransform panel = layout.Find("ExchangeItem2024");
            if (panel == null || !panel.gameObject.activeInHierarchy) return;
            RefereeController robot = SelectedRobot();
            GameManager manager = FindFirstObjectByType<GameManager>();
            if (robot == null || manager == null || manager.PriceInfo == null ||
                robot.robotClass.Value != RobotClass.Engineer) return;
            Transform grid = panel.Find("Difficulty/panel/grid");
            if (grid == null) return;
            int levels = Mathf.Min(robot.ExchangeSpeed.Count, manager.PriceInfo.silverPrice.Length,
                manager.PriceInfo.goldPrice.Length);
            for (int i = exchangeRows; i < levels; i++)
            {
                int level = i;
                GameObject row = new GameObject($"Source Exchange Level {i + 1}", typeof(RectTransform), typeof(Image), typeof(Button));
                row.transform.SetParent(grid, false);
                Image background = row.GetComponent<Image>();
                background.color = new Color(0.08f, 0.18f, 0.25f, 0.88f);
                Button button = row.GetComponent<Button>();
                button.targetGraphic = background;
                button.onClick.AddListener(() => { exchangeLevel = level; RefreshEngineerExchange(); });
                GameObject label = new GameObject("Label", typeof(RectTransform), typeof(Text));
                label.transform.SetParent(row.transform, false);
                RectTransform rect = label.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                Text text = label.GetComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.alignment = TextAnchor.MiddleCenter;
                text.color = Color.white;
                text.raycastTarget = false;
            }
            exchangeRows = Mathf.Max(exchangeRows, levels);
            for (int i = 0; i < exchangeRows; i++)
            {
                Transform row = grid.Find($"Source Exchange Level {i + 1}");
                if (row == null) continue;
                row.gameObject.SetActive(i < levels);
                if (i >= levels) continue;
                Image background = row.GetComponent<Image>();
                background.color = i == exchangeLevel ? new Color(0.08f, 0.48f, 0.62f, 0.95f) :
                    new Color(0.08f, 0.18f, 0.25f, 0.88f);
                Text text = row.GetComponentInChildren<Text>();
                if (text != null) text.text = $"{i + 1} 级  银 {manager.PriceInfo.silverPrice[i]} / 金 {manager.PriceInfo.goldPrice[i]}  · {robot.ExchangeSpeed[i]} 秒";
            }
        }

        private float nextLiveRefreshAt;

        private void Update()
        {
            if (layout == null) return;
            ReconcileRemotePurchase();
            // The supply and exchange panels read live RefereeController
            // values, but only their text needs refreshing. Four times a
            // second is indistinguishable here and avoids two full scene
            // searches plus a grid rebuild on every rendered frame.
            if (Time.unscaledTime >= nextLiveRefreshAt)
            {
                nextLiveRefreshAt = Time.unscaledTime + 0.25f;
                RectTransform small = layout.Find("SupplyItem2024");
                RectTransform large = layout.Find("HeroSupplyItem2024");
                if (small != null && small.gameObject.activeInHierarchy) RefreshAmmo(small, false);
                if (large != null && large.gameObject.activeInHierarchy) RefreshAmmo(large, true);
                RefreshEngineerExchange();
            }
            RectTransform remote = layout.Find("RemoteExchange");
            if (remote == null || !remote.gameObject.activeInHierarchy) return;
            Transform tab = remote.Find("Tab2");
            if (tab != null && !tab.gameObject.activeSelf) tab.gameObject.SetActive(true);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl) return;
            if (Input.GetKeyDown(KeyCode.Alpha1)) RemotePurchase(PurchaseType.Remote_HP);
            if (Input.GetKeyDown(KeyCode.Alpha2)) RemotePurchase(PurchaseType.Remote_Ammo0);
            if (Input.GetKeyDown(KeyCode.Alpha3)) RemotePurchase(PurchaseType.Remote_Ammo1);
        }
    }
}
