<p align="center"><img src="docs/E-Tab.png" width="96" height="96" alt="E-Tab" /></p>


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
- 右键托盘图标可以看到版本号和几个开关（勾选即表示已开启）：
  - **Start with Windows**：开机自动启动。
  - **Auto-merge new windows**：新窗口自动变成标签页；关掉后新窗口保持独立，需要时用下面的命令合并。
  - **Merge all windows (Ctrl+Shift+E)**：把当前打开的资源管理器窗口合并到最前面的那个窗口。
  - **Exit**：退出。
- 合并过程不会改动窗口的大小、位置和最大化状态；万一合并失败，窗口会原样还原。
- 日志位于 `%LOCALAPPDATA%\E-Tab\logs\E-Tab.log`。

## 许可证

MIT，保留上游 [ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility) 的版权声明，见 [LICENSE](LICENSE)。
