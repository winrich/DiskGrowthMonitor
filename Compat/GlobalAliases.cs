// 锁类型别名（global using）。
//
// 历史背景：双目标时期 net9.0 提供 System.Threading.Lock —— CLR 对 lock 语句有专门优化，
// 比锁 object 更快；net45 没有这个类型，因此用全局别名让业务代码在两个目标上都写 SyncLock。
// 2026-09-23 收敛为纯 net45 后，别名固定解析为 System.Object。
// 保留别名而不是把业务代码改回 object：一是零改动（各处的 SyncLock 原样有效），
// 二是将来若重新引入 .NET Core 目标，只需在这里补一行 #if 判断。
global using SyncLock = System.Object;
