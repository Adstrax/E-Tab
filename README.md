# E-Tab

Windows 11 File Explorer 增强工具，基于 [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility) 精简而来。

本版本只保留一个功能：**文件夹从新标签页打开**。

## 功能

- 新打开的 File Explorer 窗口自动转为已有窗口中的标签页
- 路径已打开时切换到现有标签，避免重复标签
- 保留“在文件夹中显示”等操作传入的选中项
- Windows 11 风格系统托盘菜单，右键可打开设置或退出
- 设置窗口支持开机自动启动开关
- 不包含热键、复制标签、恢复关闭标签、标签搜索、窗口快照等功能

## 环境要求

- Windows 11 22H2（Build 22621）或更高版本
- File Explorer 标签页功能已启用
- 构建需要 .NET 8 SDK

## 构建

```powershell
dotnet build -c Release
```

## 版本规则

当前版本：`1.0.6`

每次增加新功能时，需要同时：

1. 修改 `E-Tab/E-Tab.csproj` 中的 `<Version>`（并同步 `AssemblyVersion`、`FileVersion`）
2. 更新本文件的“当前版本”
3. 提交 Git 并打上对应版本号的 tag，例如 `v1.0.0`

## 许可证

MIT，保留上游 [ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility) 的版权声明，见 `LICENSE`。
