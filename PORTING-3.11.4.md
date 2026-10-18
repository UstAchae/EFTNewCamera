# SPT 3.11.4 移植记录

目标：SPT 3.11.4，EFT 0.16.1.3.35392。版本：0.1.228.314。
基于 UstAchae/EFTNewCamera 提交 `774d067df260b46470cdd77fdf86c41117f6cb77`（0.1.228）。

## 客户端接口

| 上游接口 | 35392 对应实现 |
|---|---|
| CameraManager / SettingsManager | CameraClass / SharedGameSettingsClass |
| OpticCameraManager / OpticSightStatus | GClass3403 / OpticSight.GStruct421 |
| PlayerBody.SlotView.CreateAndParent | PlayerBody.GClass2119.method_4；从方法参数取得 PlayerBody |
| Physical / Consumption / _aimMass | PlayerPhysicalClass / GClass751 / float_5 |
| OpticLookAtGlobal | ProceduralWeaponAnimation.method_6 |
| WeaponLn | 原有非公开字段，使用缓存 Harmony FieldRef 读取 |
| BallisticsCalculatorConstants / Shot 阻力函数 | GClass3449 / EftBulletClass |
| 面罩渲染与 Visor Point | VisorEffect.method_1 / gameObject_0；GClass839.BlitOrCopy |
| PlayerBridge 玩家 | iPlayer 转为 EFT.Player |
| 摄像机单例字段 | CameraClass.cameraClass |

声音补丁按 35392 的实际 POV 读取点绑定；包含 method_53、method_59、method_78，以及 method_69 / method_71 两个脚步声协程。不沿用新版方法编号猜测绑定。

瞄准歪头修复：本地玩家开启模组时，RotateHead 前缀清零独立的 blendValue，再交由原有模组后缀处理自由观察。仅清零 offset 无法屏蔽原版头颈瞄准旋转。此改动也屏蔽退出瞄准时的原版混合过渡。

## 动画策略及骨骼修订（0.1.228.314）

收到 0.1.228.313 仍会枪手脱节、噪音和枪管发红的反馈。日志没有对应异常；未确认异常时是否实际扣弹。

35392 的 Player.Init 为 MovementContext.OnStateChanged 注册回调；离开 Stationary 时调用 SetStrategy(pointOfView)，参数是 Init 捕获的旧 POV，而非当前玩家 POV。模组把玩家改为 ThirdPerson 后，该回调仍可选到 FirstPerson 策略。此时玩家和 PWA 的 POV 属性仍为 ThirdPerson，模组原有的 POV 属性检查不能发现策略不一致。

现在仅对本地玩家且总开关开启时，约束 SetStrategy(EPointOfView) 的选择；不拦截 SetStrategy(GInterface38) 所接收的显式固定武器策略，也不干预自由镜头或其他玩家。

另修正了上臂缓存按玩家 ID 常驻的问题：武器 prefab 中的第一人称手臂随换枪进入对象池，旧 Transform 仍可能不为 null。手部控制器变化时先恢复渲染标记、清空上臂列表，再从当前层级收集。

上述代码问题可以解释姿态混用和旧骨骼引用，尚未证明异常射击音/枪管发红与它们同源，不能宣称已全部解决。没有修改射速、弹药、温度、开火输入或存档。

通过 Tests/StationaryStrategyRegression.ps1 的 6 个离线场景：模拟旧 Init POV 的退出回调、普通第三人称、不同动画实例、关闭模组、自由镜头、非本地玩家。测试编译生产前缀与最小托管替身，不执行 Unity/Harmony 或枪械动画，不能替代实机回归。

### 用户实测反馈（2026-10-18）

用户确认已重启游戏测试 0.1.228.314，重复操作固定机枪/AGS 后，之前报告的问题已消失。此结果验证了本次组合修订的实际效果。未逐项撤销补丁做对照，因此不能据此区分“动画策略修正”和“上臂缓存刷新”分别解决了哪些现象，也不能推断此前是否实际消耗弹药。

日志另有 G28 动画器层数不一致记录，尚未证明与此次问题相关；没有为该记录添加修复。

## 固定武器切换修订（0.1.228.313）

收到使用场景 AGS 后普通武器瞄准异常的反馈，日志未显示对应异常。确认存在两个状态管理缺口：固定武器分支没有核对当前物品身份，且切换手部控制器后沿用对齐/头部/架枪过渡和瞄具缓存。现在按玩家、手部控制器和固定武器身份重置这些状态，并恢复由模组临时关闭的瞄具组件。

这属于针对已确认代码缺口的修订，尚未在游戏内复现原反馈或验收修复效果。回归路径：普通武器开镜 → 操作中心区场景 AGS 并瞄准 → 退出后用原武器开镜 → 切换另一把武器再开镜；同时检查铁瞄/红点/放大瞄具、重复进入退出及模组开关。

## 构建与静态验证

使用 PowerShell 7（提供 Roslyn DLL）及完整 SPT 3.11.4 游戏目录：

```powershell
pwsh -NoProfile -File Build-Local.ps1 -GameRoot "D:\games\SPT-3.11.4"
pwsh -NoProfile -File Tests/Validate-311.ps1 -GameRoot "D:\games\SPT-3.11.4"
pwsh -NoProfile -File Tests/StationaryStrategyRegression.ps1
```

构建输出 `_build/JustachaeThirdPersonCamera.dll` 和可解压到游戏根目录的 ZIP。游戏程序集不上传仓库；脚本只从本地引用。

已通过 Release 编译，以及静态检查：23 个前后置补丁签名、5 个反射字段、20 个声音方法的 POV 读取位置、2 个协程状态机及开火后坐力补丁所依赖的两个 POV 判断。

静态检查不运行 Harmony/Unity，不能保证补丁执行或最终画面正确。固定武器切换修订已有上述用户实测反馈；不代表已覆盖所有武器、地图和动作。未运行需要 Unity 2022.3 检查工程的铁瞄资源回归（见 Tests/README.md）。

## 安装和试跑

将 ZIP 中 BepInEx 文件夹合并到游戏根目录。纯客户端插件，无额外服务端前置。插件路径：`BepInEx/plugins/JustachaeThirdPersonCamera/JustachaeThirdPersonCamera.dll`。

默认关闭，在 F12 → 新镜头 → 总开关 开启。配置 GUID 沿用 `justachae.thirdpersoncamera`，不能同时安装其他目标版本的同名插件。

先在藏身处检查进入/退出瞄准、左右持枪、换弹、切换开火模式和自由观察，再检查战局、架枪、固定武器、夜视/热成像及 TAA/DLSS。
