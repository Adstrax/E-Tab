# E-Tab

Windows 11 File Explorer 增强工具。新打开的文件夹窗口会自动合并到已有窗口的标签页中，让桌面更整洁。

## 核心功能

- **文件夹自动转标签页**：新打开的 File Explorer 窗口自动转为已有窗口中的标签页
- **去重**：路径已打开时切换到现有标签，避免重复标签
- **保留选中项**：正确处理“在文件夹中显示”等操作传入的选中文件
- **极轻量**：常驻空闲内存仅数 MB，WinForms 原生托盘菜单（界面为英文）

## 系统要求

- Windows 11 22H2（Build 22621）或更高版本
- File Explorer 标签页功能已启用
- 运行需要 .NET 10 Desktop Runtime；构建需要 .NET 10 SDK

## 构建

```powershell
dotnet build -c Release
```

生成单文件发布包：

```powershell
.\pack.ps1   # 需要 PowerShell 7
```

产物为 `artifacts\E-Tab-<版本>-win64.zip`（含 `E-Tab.exe` 与 `README.txt`）。

## 使用

- 运行后驻留系统托盘并自动开始工作。
- 右键托盘图标：**Start with Windows** 开机自启动开关、**Exit** 退出（菜单项勾选即表示开机自启已开启）。
- 日志位于 `%LOCALAPPDATA%\E-Tab\logs\E-Tab.log`。

## 许可证

MIT，保留上游 [ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility) 的版权声明，见 [LICENSE](LICENSE)。