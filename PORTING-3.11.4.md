# SPT 3.11.4 移植记录

目标：SPT 3.11.4，EFT 0.16.1.3.35392。版本：0.1.228.312。
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

## 构建与静态验证

使用 PowerShell 7（提供 Roslyn DLL）及完整 SPT 3.11.4 游戏目录：

```powershell
pwsh -NoProfile -File Build-Local.ps1 -GameRoot "D:\games\SPT-3.11.4"
pwsh -NoProfile -File Tests/Validate-311.ps1 -GameRoot "D:\games\SPT-3.11.4"
```

构建输出 `_build/JustachaeThirdPersonCamera.dll` 和可解压到游戏根目录的 ZIP。游戏程序集不上传仓库；脚本只从本地引用。

已通过 Release 编译，以及静态检查：22 个前后置补丁签名、5 个反射字段、20 个声音方法的 POV 读取位置、2 个协程状态机及开火后坐力补丁所依赖的两个 POV 判断。

静态检查不运行 Harmony/Unity，不能保证补丁执行或最终画面正确。当前歪头修订尚未完成游戏内验证。未运行需要 Unity 2022.3 检查工程的铁瞄资源回归（见 Tests/README.md）。

## 安装和试跑

将 ZIP 中 BepInEx 文件夹合并到游戏根目录。纯客户端插件，无额外服务端前置。插件路径：`BepInEx/plugins/JustachaeThirdPersonCamera/JustachaeThirdPersonCamera.dll`。

默认关闭，在 F12 → 新镜头 → 总开关 开启。配置 GUID 沿用 `justachae.thirdpersoncamera`，不能同时安装其他目标版本的同名插件。

先在藏身处检查进入/退出瞄准、左右持枪、换弹、切换开火模式和自由观察，再检查战局、架枪、固定武器、夜视/热成像及 TAA/DLSS。
