// 判据资产（tools/probes/）：#89 用枪口火焰取证前，先把 B51（m249）交到本地玩家手里。
//
// 为什么单独一个文件：买枪受 CsMatch.CanBuyTime（mp_buytime=15s）+ a.InBuyZone 两个门，
// 而 4v4 起局后的等待会把这窗口睡过去 ⇒ 必须"回合刚开就买"。用它自己的 TryBuyFor（业务入口），
// 不直接改 PrimaryWeapon 字段（那会绕过库存/切枪/模型的整条链）。
//
// 用法：unity command eval_file --file tools/probes/fx-buy-m249.cs --format tsv
var m = Cs16.Module.Match.MatchModule.Instance != null
    ? Cs16.Module.Match.MatchModule.Instance.Match
    : null;
if (m == null) return "ERROR: no match (editor not in Play / no session)";
var a = m.LocalPlayer;
if (a == null) return "ERROR: no local player";

a.Money = Cs16.Core.CsConst.MaxMoney;
string r;
var ok = m.TryBuyFor(a.Id, Cs16.Core.CsWeapons.M249, out r);
if (ok) m.SwitchSlot(1);
var mag = -1;
var reserve = -1;
var w = a.ActiveWeapon ?? "-";
if (a.Ammo.TryGetValue(w, out var ammo)) { mag = ammo.inMag; reserve = ammo.reserve; }
return "buy m249=" + ok + " (" + r + ") slot=" + w + " mag=" + mag + " reserve=" + reserve + " money=" + a.Money;
