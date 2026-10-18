# 铁瞄回归检查

`Editor/IronSightRegression.cs` 在 Unity 2022.3 检查工程中运行，和生产代码共用 `IronSightAlignment.cs`。不要复制或重写一份校准算法来替代它。

检查工程需要一个只用于读取资源的 `WeaponPrefab` 组件定义，其中 `_weaponObject` 为 GameObject；组件脚本标识必须匹配被测资源。不要用检查工程重新导出武器资源。

将这两个文件放到检查工程的 `Assets/Editor/`，设置环境变量 `EFT_CAMERA_TEST_PLAN` 为 JSON 计划的绝对路径，再调用 `-batchmode -quit -executeMethod IronSightRegression.Run`。

计划格式：

```json
{
  "output": "D:/inspection/results.txt",
  "bundles": [{"key": "weapon.bundle", "path": "D:/inspection/weapon.bundle"}],
  "rigs": [{"name": "example", "parts": [
    {"id": "gun", "key": "weapon.bundle"},
    {"id": "rear", "key": "rear.bundle", "parent": "gun", "slot": "mod_sight_rear"}
  ]}]
}
```

计划必须列出全部资源和依赖，配件按父件在前排列。当前资源夹具按原版无 ModPlacer 时的装配规则（本地位置零、旋转 X=90°、缩放一）装配；存在 ModPlacer 的资源需要先扩充夹具，不能直接套用。

测试覆盖：集成标记、非铁瞄/光学瞄具绕过、旋转的武器根节点、独立配件优先级、换装后缓存刷新、隐藏标记排除、缺失标记回退，以及真实资源装配的前后标记与眼位射线共线性。实际开镜、动作过渡和游戏中的骨架姿势仍需游戏内验证。
