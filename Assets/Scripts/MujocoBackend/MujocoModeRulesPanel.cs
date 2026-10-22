using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Pacific.Mujoco
{
    // The rules this simulator actually implements, shown before the original
    // role-selection UI. Player-facing wording only: no config asset names and
    // no raw formulas. The same content with per-value citations is in
    // Doc/RM_GAME_RULES.md.
    //
    // Page counts are data-driven. The benchmark dismisses the panel by
    // clicking Next until it closes, so adding pages here is safe.
    public sealed class MujocoModeRulesPanel : MonoBehaviour
    {
        // Additive panels pause the first-person mouse capture; the FPV
        // controller checks this before its per-frame Cursor.Locked write.
        private static int openPanels;
        public static bool AnyOpen => openPanels > 0;

        private void OnEnable() => openPanels++;
        private void OnDisable() => openPanels = Mathf.Max(0, openPanels - 1);

        // Training Ground reuses the RMUC2024 performance tables but has no
        // base, outpost, exchange station or EXP table: no win, no economy,
        // no leveling. It is a driving range.
        private static readonly string[] TrainingPages =
        {
            "<size=22><color=#7FD4FF><b>训练场 · 练车场总览</b></color></size>\n\n" +
            "· 定位：RMUT 练习场，10 个 Unity 单位 = 1 米。场上没有基地、前哨站、兑换站，也不结算经验与金币，所以这里不判胜负、不会升级。\n" +
            "· 时长：420 秒，车辆就绪后自动开始，时间到直接结束；想提前走按 F10 回欢迎页。\n" +
            "· 车辆：选车菜单里可选 ARTINX2023-Hero / Engineer / Infantry / Sentry。Spawn 之后只启用你选中的那台，其它槽位不参战。\n" +
            "· 初始血量：英雄 250、步兵 200、工程 250、哨兵 400；训练场把所有车的底盘功率上限统一抬到 200，方便练车。\n" +
            "· 弹药：17mm 与 42mm 都补到 9999 发，左键可以一直打，不需要回补给区。\n" +
            "· 进入流程：欢迎页选 Training Ground → Multiplayer → Host → 进入场地先看到本规则页 → 关闭后到选车菜单选车型 → 点 Spawn。\n" +
            "· 注意：Esc 会重新打开原版选车菜单。训练场每点一次 Spawn 都会新生成一台车，已经 Spawn 过就不要再点。",

            "<size=22><color=#7FD4FF><b>训练场 · 键位速查</b></color></size>\n\n" +
            "<color=#9BE7FF>底盘与视角</color>\n" +
            "· W / S：前进 / 后退；A / D：左 / 右平移。移动方向跟随当前视角，不是车头方向。\n" +
            "· 鼠标左右：云台水平旋转，满轴 90°/秒；鼠标上下：炮管俯仰，满轴 30°/秒，范围 -10°~+45°。灵敏度在 P 设置页调。\n" +
            "· ↑ / ↓：炮管抬升 / 下压，和鼠标输入叠加，适合细调仰角。\n" +
            "· 左 Shift：加速，提高轮端功率；空格：刹车，立即切断前进和平移输入。\n" +
            "· G：切换自动跟随 / 小陀螺。自动跟随时底盘追着云台朝向走，C/V 无效。\n" +
            "· C：小陀螺逆时针，V：顺时针。按一下开始、再按一下停止，是锁定开关不是长按；切回自动跟随会清掉自旋。\n\n" +
            "<color=#9BE7FF>射击与装填</color>\n" +
            "· 鼠标左键：开火。按住会按枪口射速连续发射，受热量和弹量限制。\n" +
            "· 右 Shift：在 17mm / 42mm 之间切换当前装填；只有装了对应发射机构的车才有效。\n" +
            "· 17mm 每发 10 点伤害、10 点热量；42mm 每发 100 点伤害、100 点热量。\n\n" +
            "<color=#9BE7FF>界面与系统</color>\n" +
            "· P、=、-、*、/：打开 / 关闭设置与登录面板（车辆下拉框、Login、灵敏度、音量、键位重绑）。\n" +
            "· O：17mm 补弹面板；I：42mm 补弹面板；H：远程补给 / 工程兑换；M：大地图；按住 Tab：战况总览。\n" +
            "· Esc：关闭当前面板；F10：退出比赛回欢迎页；Alt+F4：直接退出游戏。",

            "<size=22><color=#7FD4FF><b>训练场 · 界面与面板怎么用</b></color></size>\n\n" +
            "<color=#9BE7FF>第一人称 HUD</color>\n" +
            "· 屏幕中心是准心。准心不是炮膛轴线：相机比炮口高约 0.14 米，炮口投影通常在准心下方，瞄具类提示不要强行居中修正。\n" +
            "· 左下角射手卡片只显示当前车的弹药（Shooter1 Spd / Ammo），其它车的卡片和装饰弧线已隐藏；看弹药就看这一张。\n" +
            "· HUD 还有血量、热量、功率、云台/底盘朝向和小地图；小地图编号、朝向、血量来自场上真实的裁判实体。\n" +
            "· 进入驾驶后鼠标会锁定并隐藏；按 P / O / I / H / Esc 会释放鼠标，点击游戏窗口可重新锁定。\n\n" +
            "<color=#9BE7FF>P 设置 / 登录面板</color>\n" +
            "· 打开后看 LoginWidget：在下拉框选 Red1~Red7 或 Blue1~Blue7，再点 Login（登录）切换车辆；切换会转移控制权和 FPV 机位。\n" +
            "· 下拉框里 1 是英雄、2 是工程、3~6 是步兵、7 是哨兵；本副本的 6 号位是第四台步兵，不是空中车。\n" +
            "· 同页可调鼠标灵敏度、音量；键位重绑条点一项后按新键，Esc 取消。可改前进/后退/平移/加速/刹车/开火等。\n" +
            "· 训练场切到还没生成过的槽位时，会按需在出生点创建那台车；已存在的车直接接管。\n\n" +
            "<color=#9BE7FF>补给、地图与退出</color>\n" +
            "· 训练场没有金币和补给区，O / I 面板能打开但不会完成购买；弹药本来就是 9999，不需要补。\n" +
            "· H 的远程补给同样需要金币，训练场里用不了。M 打开大地图，再按 M 或 Esc 关闭。\n" +
            "· Tab 是按住查看，松开就收起；Esc 关闭当前面板，训练场里没有面板时按 Esc 会打开/关闭原版选车菜单；F10 回欢迎页。",

            "<size=22><color=#7FD4FF><b>训练场 · 怎么把车开起来</b></color></size>\n\n" +
            "· 转向：鼠标控制云台而不是直接转车头。自动跟随模式下底盘会追着云台转，所以“看向哪里就开向哪里”；小陀螺模式下底盘独立自旋，云台保持世界朝向。\n" +
            "· 起步：按 W 前进。轮子只有碰到 Ground 层才出力，训练场已给 Field 打 Ground 标签并做直接速度驱动，正常情况下不会原地打滑。\n" +
            "· 刹车与加速：空格只切断前后/平移输入，不锁死轮子；Shift 提高驱动功率，长时间加速更容易把功率条打满。\n" +
            "· 瞄准：先把准心移到目标，再用 ↑/↓ 细调炮管。17mm 有下坠，远距离要抬炮；42mm 弹速更高，但每发 100 热量。\n" +
            "· 开火：按住左键连续射击。17mm 适合持续压制，42mm 适合点射拆建筑；热量不足一发时打不出去，需要等冷却。\n" +
            "· 换弹：右 Shift 切换 17mm / 42mm，HUD 弹药卡和准心附近的弹道提示会跟着当前弹种变化。\n" +
            "· 切车：P → 下拉框选车 → Login。切车会先关掉当前车的控制和射击，再把新车的 FPV 相机设为最高优先级。\n" +
            "· 视角：本副本相机固定在 FPV mount 上，底盘侧倾不会带歪视角；没有第三人称自由视角。",

            "<size=22><color=#7FD4FF><b>训练场 · 四种车的特点与用处</b></color></size>\n\n" +
            "<color=#9BE7FF>英雄 · Hero</color>\n" +
            "· 武器：42mm，单发 100 伤害、每发 100 热量；开局 10 发，训练场补到 9999。射速慢、弹量少，但拆前哨站/基地和秒步兵效率最高。\n" +
            "· 血量 250；底盘默认血量优先。定位是远程重炮：卡住掩体，等队友给视野，点射关键目标。\n" +
            "· 用法：不要冲最前面。先用 17mm 车辆消耗，再用 42mm 收人头或拆建筑；注意热量，连打两发就进入危险区。\n\n" +
            "<color=#9BE7FF>工程 · Engineer</color>\n" +
            "· 武器：没有发射机构，不能射击；血量 250，功率不限，是场上最快的车。\n" +
            "· 定位：经济与支援核心。2023 / 2024 里负责占矿区、把矿石运回兑换站、占领或激活能量机关；2024 工程车按 H 打开工程兑换面板选档。\n" +
            "· 用法：开局抢矿，别走直线；被追时利用不限功率甩开；没队友掩护不要单独进敌方半场。碰撞只有 2 点伤害，别想着撞人。\n\n" +
            "<color=#9BE7FF>步兵 · Infantry</color>\n" +
            "· 武器：17mm，单发 10 伤害、每发 10 热量；开局 50 发，训练场补到 9999。持续输出、热量压力小，是控点和消耗的主力。\n" +
            "· 血量 200；训练场功率上限统一 200。2023 比赛里固定平衡型（300 血 / 60 功率），2024 比赛里固定血量优先（200 血 / 45 功率）。\n" +
            "· 本副本每方有 3~6 共四个步兵槽位。用法：两三人一组拉枪线，集火哨兵/前哨站；用 A/D 横移躲 42mm，别和英雄正面对炮。\n\n" +
            "<color=#9BE7FF>哨兵 · Sentry</color>\n" +
            "· 武器：17mm。训练场 9999 发；2024 开局满级并带 400 发；2023 是 1000 血 / 150 功率的固定炮台。\n" +
            "· 定位：守家、守前哨站、封走位。本副本只有被你选中的哨兵才会开火，平时不会自动射击。\n" +
            "· 用法：放在基地/前哨站的火力走廊，别追人；哨兵不能复活，丢了就没了。\n" +
            "· 注意：本副本没有空中机器人，也没有雷达车；6 号位是第四台步兵，2024 的雷达标记规则存在但场上没有车能触发。",

            "<size=22><color=#7FD4FF><b>训练场 · 练习清单与本版细节</b></color></size>\n\n" +
            "<color=#9BE7FF>建议练什么</color>\n" +
            "· 8 字绕桩：W/A/S/D 配鼠标转向，练自动跟随下的车头跟随和贴边。\n" +
            "· 小陀螺瞄准：G 切小陀螺，C/V 起旋，练底盘转、准心稳；再按 G 回跟随，确认自旋停止。\n" +
            "· 热量管理：17mm 连打看热量条；42mm 打两发就停，等冷却再打，避免超热扣血。\n" +
            "· 功率管理：Shift 长按加速，看缓冲能量和功率条，学会松油门回充。\n" +
            "· 换弹与切车：右 Shift 切 17/42；P 面板切 Red1~Red7，确认相机、弹药卡、小地图编号都跟着换。\n\n" +
            "<color=#9BE7FF>本副本的实装细节</color>\n" +
            "· 训练场只生成选中的一台车；按 P 切到未生成的槽位才会创建新车。其它槽位的车隐藏模型，但仍保留刚体和碰撞。\n" +
            "· 原版轮式物理在练习场偏软，本副本已给 Field 打 Ground 标签、直接速度驱动，并把功率上限抬到 200；这不是正式比赛手感。\n" +
            "· 无胜负、无金币、无经验、无复活；时间到或 F10 离开。补给、兑换面板都保留，但训练场没有经济来源，购买不会成功。\n" +
            "· 超热仍会扣血：热量每超出上限 250 点扣 10% 上限血；超过上限 2 倍后，超出部分每 250 点直接扣一整条血。"
        };

        private static readonly string[] Match2023Pages =
        {
            "<size=22><color=#7FD4FF><b>RMUC2023 · 赛制与胜负</b></color></size>\n\n" +
            "· 进入：欢迎页选 RMUC2023 → Multiplayer → Host → 进入场地先看到本规则页 → 关闭后在选车菜单点红方或蓝方的车型按钮，接管对应的车。\n" +
            "· 时长 420 秒，车辆全部就绪后自动开始；只有基地被毁或时间耗尽两种结束方式，没有加时、没有平局，时间到不比较分数。\n" +
            "· 基地：5000 血 + 250 护盾，开局无敌；前哨站：1500 血；哨兵：1000 血 / 150 功率。\n" +
            "· 击毁顺序固定：己方前哨站被毁 → 己方哨兵与基地解除无敌；己方哨兵也被毁 → 基地护盾清零；基地被毁 → 判负。\n" +
            "· 前哨站开赛满 3 分钟后停转；前 3 分钟内打掉它 500 血，最后攻击者得 25 经验（每局一次）。\n" +
            "· 建筑不吃减伤；打到自己人照样掉血，只是不给经验。命中没有最低初速要求，擦到就算。\n" +
            "· 本副本每方 7 台：1 英雄、2 工程、3~6 步兵、7 哨兵；没有空中车和雷达车，6 号位是第四台步兵。\n" +
            "· 选车菜单有 7 个车型按钮，点哪个就接管哪个槽位；车已经在场上，不会再复制一台。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 键位速查</b></color></size>\n\n" +
            "<color=#9BE7FF>底盘与视角</color>\n" +
            "· W / S：前进 / 后退；A / D：左 / 右平移。移动方向跟随当前视角，不是车头方向。\n" +
            "· 鼠标左右：云台水平旋转，满轴 90°/秒；鼠标上下：炮管俯仰，满轴 30°/秒，范围 -10°~+45°。灵敏度在 P 设置页调。\n" +
            "· ↑ / ↓：炮管抬升 / 下压，和鼠标输入叠加。左 Shift：加速；空格：刹车（切断前进/平移）。\n" +
            "· G：切换自动跟随 / 小陀螺；C / V：小陀螺逆/顺时针，按一下开始、再按一下停止。\n\n" +
            "<color=#9BE7FF>射击与补给</color>\n" +
            "· 鼠标左键：开火；按住连续发射。右 Shift：切换 17mm / 42mm 装填。\n" +
            "· O：17mm 补弹面板；I：42mm 补弹面板。开进己方补给区后打开，用 +/- 选数量（17mm 10/20/50/100，42mm 1/2/5/10），点 Confirm 后再点弹窗 OK。\n" +
            "· H：远程补给面板。面板打开时按 Ctrl+1 回血、Ctrl+2 补 17mm、Ctrl+3 补 42mm，也可以直接点 Item1/2/3。\n" +
            "· 远程补给下单后 6 秒生效；下单前 6 秒内不能受伤或开火。\n\n" +
            "<color=#9BE7FF>界面与系统</color>\n" +
            "· P、=、-、*、/：设置与登录面板（车辆下拉框、Login、灵敏度、音量、键位重绑）。\n" +
            "· 下拉框选 Red1~Red7 或 Blue1~Blue7，再点 Login 切车；1 英雄、2 工程、3~6 步兵、7 哨兵。\n" +
            "· 按住 Tab：战况总览；M：大地图；Esc：关闭当前面板；F10：退出比赛回欢迎页；Alt+F4：退出游戏。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 瞄准、热量与功率</b></color></size>\n\n" +
            "<color=#9BE7FF>伤害</color>\n" +
            "· 17mm 命中 10 点，42mm 命中 100 点，碰撞 2 点；打在任何部位伤害都一样，没有部位减伤。\n" +
            "· 42mm 是固定 100 伤害，不按初速度百分比扣血；打建筑也不吃减伤。\n" +
            "· 打到自己人照样掉血，只是不结算经验；打敌方建筑和机器人正常计算。\n\n" +
            "<color=#9BE7FF>热量</color>\n" +
            "· 每发 17mm +10 热量，42mm +100 热量；热量随时间冷却，冷却速度由枪口配置决定（本副本不能选枪口模式，用预制体固定值）。\n" +
            "· 超热扣血：热量每超出上限 250 点，扣 10% 上限血；一旦超过上限的 2 倍，超出部分每 250 点直接扣一整条血，并把热量压回 2 倍上限。\n" +
            "· 剩余热量不足一发时打不出去；连续打 42mm 两发很容易进入超热区。\n\n" +
            "<color=#9BE7FF>功率</color>\n" +
            "· 底盘功率超出上限时，先消耗缓冲能量，再消耗 1900 点车载能量，两者都空了才开始扣血。\n" +
            "· 扣血按超出比例每 0.1 秒结算：不到一成扣 10% 上限血，不到两成扣 20%，超过两成扣 40%。\n" +
            "· 功率有余量时会回充缓冲和能量；工程车不限功率。\n" +
            "· 瞄准手感：鼠标满轴 90°/s 云台、30°/s 炮管，俯仰 -10°~+45°；17mm 有下坠，远距离抬炮，42mm 弹道更直。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 每台车的特点与用处</b></color></size>\n\n" +
            "本副本每方 7 个槽位：1 英雄、2 工程、3~6 四个步兵、7 哨兵。没有空中车和雷达车。\n\n" +
            "<color=#9BE7FF>英雄 · 1 号位</color>\n" +
            "· 42mm 重炮，单发 100 伤害、100 热量；开局 10 发 42mm。一级 250 血 / 55 功率，三级 450 血 / 65 功率（血量优先）。\n" +
            "· 定位：远程拆建筑和秒杀脆皮。站掩体后点射前哨站、基地和哨兵，不要贴脸。\n" +
            "· 狙击点：英雄踩上狙击点会获得狙击增益，增益期间每开一炮额外 +10 金币。\n\n" +
            "<color=#9BE7FF>工程 · 2 号位</color>\n" +
            "· 没有发射机构，不能射击；250 血、功率不限，是场上最快的车。\n" +
            "· 定位：占矿区、运矿、兑换、占控制点/增益点。矿石带回己方控制下的兑换站会自动结算，不需要额外按键。\n" +
            "· 用法：开局抢矿；被追时利用不限功率甩开；不要单独冲敌方半场。碰撞只有 2 点伤害。\n\n" +
            "<color=#9BE7FF>步兵 · 3~6 号位</color>\n" +
            "· 17mm，单发 10 伤害、10 热量；开局 50 发。一级平衡型 300 血 / 60 功率，三级 500 血 / 100 功率。\n" +
            "· 定位：控点、消耗、集火哨兵和前哨站。四个步兵槽位可以同时存在，按 P 面板分别切换。\n" +
            "· 用法：两三人一组拉枪线；用 A/D 横移躲 42mm；绕侧翼贴近英雄，别在开阔地正面对炮。\n\n" +
            "<color=#9BE7FF>哨兵 · 7 号位</color>\n" +
            "· 1000 血 / 150 功率，17mm 固定炮台。本副本只有被你选中的哨兵才会开火，平时不自动射击。\n" +
            "· 定位：守基地、守前哨站、封走位；不能复活。把火力走廊压在基地入口，不要追人。\n" +
            "· 注意：前哨站被毁前哨兵和基地是无敌的；想打基地，先按前哨站 → 哨兵 → 基地的顺序拆。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 金币、补给与兑换</b></color></size>\n\n" +
            "<color=#9BE7FF>金币</color>\n" +
            "· 开局每方 400 金币；比赛剩 6、5、4、3、2 分钟时各发 50，剩 1 分钟时发 150。\n" +
            "· 全场第一块金矿兑换时额外多给 250 金币；击毁敌方机器人不给金币。\n" +
            "· 金币主要来源：定时发放 + 工程车运矿兑换。\n\n" +
            "<color=#9BE7FF>买弹</color>\n" +
            "· 17mm 每发 1 金币，42mm 每发 15 金币；队伍库存 17mm 1500 发、42mm 100 发（本副本实装 42mm 库存是 1500，基本打不完）。\n" +
            "· 补弹必须在己方补给区：把车开进补给区 → 按 O（17mm）或 I（42mm）→ 用 +/- 调数量 → Confirm → 弹窗 OK。\n" +
            "· 面板会显示“已拥有/队伍库存”，库存或金币不足时 Confirm 会报错。\n\n" +
            "<color=#9BE7FF>矿石兑换</color>\n" +
            "· 工程车占领矿区后每次产一块矿；把矿带回己方控制下的兑换站，站内会自动逐块结算。\n" +
            "· 银矿 75 起、金矿 100 起；兑换累计额跨过 1100 金币后单价 ×1.3，跨过 1625 后 ×2。\n" +
            "· 矿石在手里等得越久越亏：15 秒内不亏，等满 50 秒以上就换不到钱。\n" +
            "· 第一块金矿额外 +250；别把金矿留到 50 秒以后。\n\n" +
            "<color=#9BE7FF>远程补给</color>\n" +
            "· 每种补给限用 2 次（回血 / 17mm / 42mm 分开计算）；下单后 6 秒生效，下单前 6 秒内不能受伤或开火。\n" +
            "· 回血 100 金币（每过一分钟涨 20）回 60% 血；17mm 200 金币 +100 发；42mm 300 金币 +10 发。\n" +
            "· 按 H 打开远程补给面板，点 Item1/2/3 或按 Ctrl+1/2/3 下单。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 成长与复活</b></color></size>\n\n" +
            "<color=#9BE7FF>三级成长</color>\n" +
            "· 英雄血量优先：250 / 350 / 450 血，功率 55 / 60 / 65；步兵平衡型：300 / 400 / 500 血，功率 60 / 80 / 100。\n" +
            "· 工程固定 250 血、不限功率；哨兵固定 1000 血 / 150 功率，不成长。\n" +
            "· 经验随时间自动增长：英雄每秒 6 点，步兵每秒 3 点；升级门槛英雄 60 / 140，步兵 30 / 90。\n" +
            "· 经验还来自击杀、助攻、以及前 3 分钟内对前哨站造成 500 伤害（最后攻击者得 25）。\n" +
            "· 击杀：击杀者得到对方的经验值，首杀额外 +50；助攻者各得四分之一；打自己人不给经验。\n" +
            "· 升级同时提升血量上限、功率上限和枪口热量上限，并立刻补满新增的血量。\n\n" +
            "<color=#9BE7FF>死亡与复活</color>\n" +
            "· 阵亡后读条：10 秒起步，比赛每过去 10 秒多等 1 秒——开局阵亡等 52 秒，最后一分钟阵亡只等 10 秒。\n" +
            "· 买活价格 = 已开赛分钟数 × 100 +（车辆等级 + 1）× 50 金币；每买活一次，后续读条再延长 20 秒。\n" +
            "· 免费复活只回 10% 血；买活回满血并解锁发射机构。复活原地起身，有短暂无敌。\n" +
            "· 哨兵、基地、前哨站不会复活。",

            "<size=22><color=#7FD4FF><b>RMUC2023 · 场地机制与实装细节</b></color></size>\n\n" +
            "<color=#9BE7FF>建筑与压制</color>\n" +
            "· 前哨站开赛满 3 分钟停转；前 3 分钟内打掉 500 血，最后攻击者得 25 经验（每局一次）。\n" +
            "· 控制点：一方单独占领满 6 秒，压制对方前哨站，让它转速减半；双方都不占或都占时压制解除。\n" +
            "· 击毁顺序不能跳：先破前哨站，再破哨兵，最后才能打掉基地护盾并击毁基地。\n\n" +
            "<color=#9BE7FF>矿区与增益</color>\n" +
            "· 矿区：工程车占领后产矿，每次一块；矿石带回己方兑换站自动换金币。\n" +
            "· 狙击点：英雄踩上获得狙击增益，增益期间每开一炮额外 +10 金币。\n" +
            "· 增益点：踩上直接拿增益；持续时间与强度以场上 HUD 为准。\n\n" +
            "<color=#9BE7FF>本副本的实装细节</color>\n" +
            "· 42mm 队伍库存实际是 1500 发（规则写 100），基本够打完整局。\n" +
            "· 42mm 固定 100 伤害；命中没有最低初速要求，擦到就掉血。\n" +
            "· 没有飞镖伤害、没有违规罚分、没有加时和拼点；补给区回血速度固定，不会因脱战提速。\n" +
            "· 小地图：M 打开大地图，Tab 按住看战况；编号与场上裁判实体一致。",
        };

        private static readonly string[] Match2024Pages =
        {
            "<size=22><color=#7FD4FF><b>RMUC2024 · 赛制与胜负</b></color></size>\n\n" +
            "· 进入：欢迎页选 RMUC2024 → Multiplayer → Host → 进入场地先看到本规则页 → 关闭后在选车菜单点红方或蓝方的车型按钮，接管对应的车。\n" +
            "· 时长 420 秒，车辆全部就绪后自动开始；只有基地被毁或时间耗尽两种结束方式，没有加时、没有平局，时间到不比较分数。\n" +
            "· 基地：5000 血 + 250 护盾，开局无敌；前哨站：1500 血；哨兵：400 血，开局满级并带 400 发弹。\n" +
            "· 击毁顺序：己方前哨站被毁 → 己方哨兵解除无敌并补回 600 血；己方哨兵也被毁 → 基地护盾清零；基地被毁 → 判负。\n" +
            "· 前哨站开赛满 3 分钟后停转；前 3 分钟内打掉它 500 血，最后攻击者得 100 经验（每局一次）。\n" +
            "· 建筑不吃减伤；打到自己人照样掉血，只是不给经验。命中没有最低初速要求。\n" +
            "· 本副本每方 7 台：1 英雄、2 工程、3~6 步兵、7 哨兵；没有空中车和雷达车，6 号位是第四台步兵。\n" +
            "· 选车菜单有 7 个车型按钮，点哪个就接管哪个槽位；车已经在场上，不会再复制一台。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 键位速查</b></color></size>\n\n" +
            "<color=#9BE7FF>底盘与视角</color>\n" +
            "· W / S：前进 / 后退；A / D：左 / 右平移。移动方向跟随当前视角，不是车头方向。\n" +
            "· 鼠标左右：云台水平旋转，满轴 90°/秒；鼠标上下：炮管俯仰，满轴 30°/秒，范围 -10°~+45°。灵敏度在 P 设置页调。\n" +
            "· ↑ / ↓：炮管抬升 / 下压，和鼠标输入叠加。左 Shift：加速；空格：刹车（切断前进/平移）。\n" +
            "· G：切换自动跟随 / 小陀螺；C / V：小陀螺逆/顺时针，按一下开始、再按一下停止。\n\n" +
            "<color=#9BE7FF>射击与补给</color>\n" +
            "· 鼠标左键：开火；按住连续发射。右 Shift：切换 17mm / 42mm 装填。\n" +
            "· O：17mm 补弹面板；I：42mm 补弹面板。开进己方补给区后打开，用 +/- 选数量（17mm 10/20/50/100，42mm 1/2/5/10），点 Confirm 后再点弹窗 OK。\n" +
            "· H：普通车打开远程补给面板（Ctrl+1 回血、Ctrl+2 补 17mm、Ctrl+3 补 42mm）；RMUC2024 的工程车按 H 打开工程兑换面板。\n" +
            "· 远程补给下单后 6 秒生效；下单前 6 秒内不能受伤或开火。\n\n" +
            "<color=#9BE7FF>界面与系统</color>\n" +
            "· P、=、-、*、/：设置与登录面板（车辆下拉框、Login、灵敏度、音量、键位重绑）。\n" +
            "· 下拉框选 Red1~Red7 或 Blue1~Blue7，再点 Login 切车；1 英雄、2 工程、3~6 步兵、7 哨兵。\n" +
            "· 按住 Tab：战况总览；M：大地图；Esc：关闭当前面板；F10：退出比赛回欢迎页；Alt+F4：退出游戏。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 瞄准、热量与功率</b></color></size>\n\n" +
            "<color=#9BE7FF>伤害</color>\n" +
            "· 17mm 命中 10 点，42mm 命中 100 点，碰撞 2 点；打在任何部位伤害都一样，没有部位减伤。\n" +
            "· 42mm 是固定 100 伤害，不按初速度百分比扣血；打建筑也不吃减伤。\n" +
            "· 打到自己人照样掉血，只是不结算经验；命中只看接触，擦到就算。\n\n" +
            "<color=#9BE7FF>热量</color>\n" +
            "· 每发 17mm +10 热量，42mm +100 热量；热量随时间冷却，冷却速度由枪口配置决定（本副本不能选枪口模式，用预制体固定值）。\n" +
            "· 超热扣血：热量每超出上限 250 点，扣 10% 上限血；一旦超过上限的 2 倍，超出部分每 250 点直接扣一整条血，并把热量压回 2 倍上限。\n" +
            "· 剩余热量不足一发时打不出去；42mm 连打两发很容易进入超热区。\n\n" +
            "<color=#9BE7FF>功率</color>\n" +
            "· 底盘功率超出上限时，先消耗缓冲能量，再消耗 1900 点车载能量，两者都空了才开始扣血。\n" +
            "· 扣血按超出比例每 0.1 秒结算：不到一成扣 10% 上限血，不到两成扣 20%，超过两成扣 40%。\n" +
            "· 功率有余量时会回充缓冲和能量；工程车不限功率。\n" +
            "· 瞄准手感：鼠标满轴 90°/s 云台、30°/s 炮管，俯仰 -10°~+45°；17mm 有下坠，远距离抬炮，42mm 弹道更直。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 每台车的特点与用处</b></color></size>\n\n" +
            "本副本每方 7 个槽位：1 英雄、2 工程、3~6 四个步兵、7 哨兵。没有空中车和雷达车。\n\n" +
            "<color=#9BE7FF>英雄 · 1 号位</color>\n" +
            "· 42mm 重炮，单发 100 伤害、100 热量；开局 10 发 42mm。一级 250 血 / 55 功率，十级 500 血 / 120 功率（血量优先）。\n" +
            "· 定位：远程拆建筑和秒杀脆皮。卡掩体点射前哨站、基地和哨兵；利用高地和基地增益区拉距离。\n" +
            "· 狙击增益：英雄带狙击增益时每开一炮额外 +10 金币（2024 经济）。\n\n" +
            "<color=#9BE7FF>工程 · 2 号位</color>\n" +
            "· 没有发射机构，不能射击；250 血、功率不限，是场上最快的车。\n" +
            "· 定位：占矿区、运矿、兑换、启动能量机关与占增益区；2024 工程车按 H 打开工程兑换面板，选档后 Confirm。\n" +
            "· 用法：开局抢矿；被追时利用不限功率甩开；不要单独冲敌方半场。碰撞只有 2 点伤害。\n\n" +
            "<color=#9BE7FF>步兵 · 3~6 号位</color>\n" +
            "· 17mm，单发 10 伤害、10 热量；开局 50 发。一级 200 血 / 45 功率（血量优先），十级 400 血 / 100 功率。\n" +
            "· 定位：控点、消耗、集火哨兵和前哨站。四个步兵槽位可以同时存在，按 P 面板分别切换。\n" +
            "· 用法：两三人一组拉枪线；用 A/D 横移躲 42mm；绕侧翼贴近英雄，别在开阔地正面对炮。\n\n" +
            "<color=#9BE7FF>哨兵 · 7 号位</color>\n" +
            "· 400 血、100 功率，开局满级并带 400 发 17mm；己方前哨站被毁后解除无敌并补回 600 血。\n" +
            "· 定位：守基地、守前哨站、封走位；不能复活。本副本只有被你选中的哨兵才会开火，平时不自动射击。\n" +
            "· 用法：放在基地/前哨站的火力走廊；前哨站丢了以后哨兵会变成可攻击目标，优先保住它。\n" +
            "· 注意：2024 的雷达标记规则存在，但本副本没有雷达车，场上没有车能触发标记。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 金币、补给与兑换</b></color></size>\n\n" +
            "<color=#9BE7FF>金币</color>\n" +
            "· 开局每方 400 金币；剩 6、5、4、3、2 分钟时各发 50，剩 1 分钟时发 150。\n" +
            "· 第一块金矿兑换额外 +250；英雄带狙击增益时每开一炮额外 +10 金币；击毁敌方机器人不给金币。\n\n" +
            "<color=#9BE7FF>买弹</color>\n" +
            "· 17mm 每发 1 金币，42mm 每发 15 金币；本副本每局开局 42mm 队伍库存实际是 1500 发，基本打不完。\n" +
            "· 补弹必须在己方补给区：把车开进补给区 → 按 O（17mm）或 I（42mm）→ 用 +/- 调数量 → Confirm → 弹窗 OK。\n" +
            "· 面板会显示“已拥有/队伍库存”，库存或金币不足时 Confirm 会报错。\n\n" +
            "<color=#9BE7FF>矿石兑换</color>\n" +
            "· 工程车占领矿区后产矿；把矿带回己方兑换站结算。银矿 75 起、金矿 200 起；累计兑换额越高单价越高，最高 ×2。\n" +
            "· 矿石在手里等得越久越亏：15 秒内不亏，等满 50 秒以上就换不到钱。\n" +
            "· 2024 工程车按 H 打开工程兑换面板：点 1~N 级档位，再点 Confirm 确认；Cancel 取消。\n\n" +
            "<color=#9BE7FF>远程补给</color>\n" +
            "· 2024 远程补给次数不限；每个项目下单后 6 秒生效，下单前 6 秒内不能受伤或开火。\n" +
            "· 回血 50 金币（每过一分钟涨 20）回 60% 血；17mm 150 金币 +100 发；42mm 200 金币 +10 发。\n" +
            "· 按 H 打开远程补给面板（工程车除外），点 Item1/2/3 或按 Ctrl+1/2/3 下单。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 成长与复活</b></color></size>\n\n" +
            "<color=#9BE7FF>十级成长</color>\n" +
            "· 英雄血量优先：一级 250 血 / 55 功率，十级 500 血 / 120 功率；步兵血量优先：一级 200 血 / 45 功率，十级 400 血 / 100 功率。\n" +
            "· 工程固定 250 血、不限功率；哨兵固定 400 血 / 100 功率，开局满级，不成长。\n" +
            "· 升一级要 550 点经验，最后一级 600；没有随时间自动增长的经验，只能靠打。\n" +
            "· 经验来源：17mm 每发 1 点、42mm 每发 10 点；造成伤害按伤害的 4 倍计（打建筑只有 1 倍）；击杀/助攻；首次踩上能量机关点得 300 点。\n" +
            "· 小增益期间全队经验翻倍，双倍部分上限 800 点；平衡型车辆经验 1.5 倍（本副本步兵固定血量优先，不享受这条）。\n" +
            "· 升级同时提升血量上限、功率上限和枪口热量上限，并立刻补满新增的血量。\n\n" +
            "<color=#9BE7FF>死亡与复活</color>\n" +
            "· 阵亡后读条：10 秒起步，比赛每过去 10 秒多等 1 秒——开局阵亡等 52 秒，最后一分钟阵亡只等 10 秒。\n" +
            "· 买活价格 = 已开赛分钟数 × 80 +（车辆等级 + 1）× 20 金币，比 2023 便宜；每买活一次，后续读条再延长 20 秒。\n" +
            "· 免费复活只回 10% 血；买活回满血并解锁发射机构。复活原地起身，有短暂无敌。\n" +
            "· 哨兵、基地、前哨站不会复活。",

            "<size=22><color=#7FD4FF><b>RMUC2024 · 场地机制与实装细节</b></color></size>\n\n" +
            "<color=#9BE7FF>场地区域</color>\n" +
            "· 场上有 3 处矿区、6 处高地、2 处基地增益区、2 处能量机关点，另有回血补给区。\n" +
            "· 回血区每秒回复 10% 血量，进区同时解锁发射机构；回血速度固定，不会因脱战提速。\n" +
            "· 增益区会随时间升档：比赛剩 5 分钟、4 分钟、3 分钟时各升一次，越往后增益越强。\n\n" +
            "<color=#9BE7FF>能量机关与大增益</color>\n" +
            "· 能量机关要先占领己方的激活区才能启动；首次踩上能量机关点得 300 经验。\n" +
            "· 大增益按能量机关的命中得分给攻击加成：15 分及以下 50%，16~25 分 55%，26~35 分 60%，36~40 分 100%，41~45 分 200%，46 分 240%，47 分 280%，48 分 320%，49 分 360%，50 分 400%；持续 45 秒。\n" +
            "· 小增益期间全队经验翻倍，双倍部分上限 800 点。\n\n" +
            "<color=#9BE7FF>本副本的实装细节</color>\n" +
            "· 42mm 队伍库存实际是 1500 发（规则写 100），基本够打完整局；42mm 固定 100 伤害。\n" +
            "· 没有飞镖伤害、没有违规罚分、没有加时和拼点；命中没有最低初速要求。\n" +
            "· 雷达标记规则存在，但本副本没有雷达车，无法触发；没有空中机器人，6 号位是第四台步兵。\n" +
            "· 小地图：M 打开大地图，Tab 按住看战况；编号与场上裁判实体一致。",
        };

        private string[] pages;
        private int page;
        private Text body;
        private Text progress;
        private Text nextLabel;
        private Text prevLabel;
        private Button prevButton;
        private Image prevBackground;
        private ScrollRect bodyScroll;

        public static void Show(Scene scene)
        {
            if (scene.name != "Training Ground" && scene.name != "TrainingGround" &&
                scene.name != "RMUC2023" && scene.name != "RMUC2024") return;
            if (FindFirstObjectByType<MujocoModeRulesPanel>() != null) return;

            var root = new GameObject("MuJoCo Mode Rules", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(MujocoModeRulesPanel));
            root.GetComponent<MujocoModeRulesPanel>().Build(scene.name);
        }

        private void Build(string sceneName)
        {
            bool training = sceneName.StartsWith("Training", StringComparison.OrdinalIgnoreCase);
            string mode = training ? "Training Ground 训练场" : sceneName + " 比赛模式";
            pages = training
                ? TrainingPages
                : sceneName.IndexOf("2023", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Match2023Pages
                    : Match2024Pages;

            Canvas canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Font font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, 24);
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            RectTransform shade = Panel(transform, "Backdrop", new Color(0.02f, 0.04f, 0.08f, 0.92f));
            Stretch(shade, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            RectTransform card = Panel(shade, "Rules Card", new Color(0.07f, 0.12f, 0.19f, 1f));
            Stretch(card, new Vector2(0.05f, 0.035f), new Vector2(0.95f, 0.965f), Vector2.zero, Vector2.zero);

            Text title = Label(card, "Title", mode, font, 32, TextAnchor.MiddleLeft);
            Stretch(title.rectTransform, new Vector2(0.035f, 0.845f), new Vector2(0.965f, 0.965f),
                Vector2.zero, Vector2.zero);
            RectTransform divider = Panel(card, "Title Divider", new Color(0.20f, 0.42f, 0.58f, 0.55f));
            Stretch(divider, new Vector2(0.035f, 0.838f), new Vector2(0.965f, 0.842f),
                Vector2.zero, Vector2.zero);

            // A scroll view keeps long rule pages readable instead of shrinking
            // the face until the text is too small to read. The transparent
            // viewport image is the wheel-event target for the ScrollRect.
            RectTransform viewport = Panel(card, "Rules Viewport", new Color(0f, 0f, 0f, 0f));
            Stretch(viewport, new Vector2(0.035f, 0.145f), new Vector2(0.965f, 0.835f),
                Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();
            bodyScroll = viewport.gameObject.AddComponent<ScrollRect>();
            bodyScroll.horizontal = false;
            bodyScroll.vertical = true;
            bodyScroll.movementType = ScrollRect.MovementType.Clamped;
            bodyScroll.scrollSensitivity = 45f;
            bodyScroll.inertia = true;
            bodyScroll.decelerationRate = 0.12f;
            bodyScroll.viewport = viewport;

            body = Label(viewport, "Rules Text", "", font, 18, TextAnchor.UpperLeft);
            RectTransform bodyRect = body.rectTransform;
            bodyRect.anchorMin = new Vector2(0f, 1f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.pivot = new Vector2(0.5f, 1f);
            bodyRect.anchoredPosition = Vector2.zero;
            bodyRect.sizeDelta = Vector2.zero;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            body.lineSpacing = 1.28f;
            body.supportRichText = true;
            body.raycastTarget = false;
            ContentSizeFitter fitter = body.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            bodyScroll.content = bodyRect;

            progress = Label(card, "Page Number", "", font, 17, TextAnchor.MiddleLeft);
            Stretch(progress.rectTransform, new Vector2(0.035f, 0.04f), new Vector2(0.52f, 0.12f),
                Vector2.zero, Vector2.zero);

            prevBackground = Panel(card, "Previous Page", new Color(0.13f, 0.17f, 0.22f, 1f))
                .GetComponent<Image>();
            RectTransform prevRect = prevBackground.rectTransform;
            Stretch(prevRect, new Vector2(0.56f, 0.04f), new Vector2(0.72f, 0.12f),
                Vector2.zero, Vector2.zero);
            prevButton = prevRect.gameObject.AddComponent<Button>();
            prevButton.onClick.AddListener(Previous);
            prevLabel = Label(prevRect, "Button Text", "上一页", font, 20, TextAnchor.MiddleCenter);
            Stretch(prevLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            prevLabel.raycastTarget = false;

            RectTransform nextRect = Panel(card, "Next Page", new Color(0.12f, 0.55f, 0.82f, 1f));
            Stretch(nextRect, new Vector2(0.75f, 0.04f), new Vector2(0.965f, 0.12f),
                Vector2.zero, Vector2.zero);
            Button nextButton = nextRect.gameObject.AddComponent<Button>();
            nextButton.onClick.AddListener(Next);
            nextLabel = Label(nextRect, "Button Text", "", font, 22, TextAnchor.MiddleCenter);
            Stretch(nextLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            nextLabel.raycastTarget = false;

            ShowPage();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            // Interactive pages always open with the pointer in the middle of
            // the screen, never wherever the last game view left it.
            if (Application.isFocused && Mouse.current != null)
                Mouse.current.WarpCursorPosition(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
        }

        private void Next()
        {
            if (page >= pages.Length - 1) { Destroy(gameObject); return; }
            page++;
            ShowPage();
        }

        private void Previous()
        {
            if (page <= 0) return;
            page--;
            ShowPage();
        }

        private void ShowPage()
        {
            body.text = pages[page];
            progress.text = $"第 {page + 1} / {pages.Length} 页 · 滚轮滚动";
            nextLabel.text = page == pages.Length - 1 ? "开始选车" : "下一页";
            bool hasPrevious = page > 0;
            prevButton.interactable = hasPrevious;
            prevBackground.color = hasPrevious
                ? new Color(0.16f, 0.30f, 0.42f, 1f)
                : new Color(0.11f, 0.14f, 0.19f, 1f);
            prevLabel.color = hasPrevious ? Color.white : new Color(0.48f, 0.53f, 0.60f, 1f);
            // Every page starts at its top edge; otherwise a scroll position
            // from the previous page survives the text swap (the content still
            // has the old height until the fitter rebuilds).
            bodyScroll.StopMovement();
            LayoutRebuilder.ForceRebuildLayoutImmediate(body.rectTransform);
            Canvas.ForceUpdateCanvases();
            bodyScroll.verticalNormalizedPosition = 1f;
            // Force the top edge even if ScrollRect still held the previous
            // page's content bounds when the text changed.
            body.rectTransform.anchoredPosition =
                new Vector2(body.rectTransform.anchoredPosition.x, 0f);
        }

        private static RectTransform Panel(Transform parent, string name, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = color;
            return obj.GetComponent<RectTransform>();
        }

        private static Text Label(Transform parent, string name, string content, Font font, int size, TextAnchor anchor)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Text));
            obj.transform.SetParent(parent, false);
            Text label = obj.GetComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.color = Color.white;
            label.alignment = anchor;
            label.text = content;
            return label;
        }

        private static void Stretch(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
