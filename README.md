<p align="center"><img src="docs/E-Tab.png" width="96" height="96" alt="E-Tab" /></p>


# E-Tab

Windows 11 File Explorer 增强工具。新打开的文件夹窗口会自动合并到已有窗口的标签页中，让桌面更整洁。

## 核心功能

- **文件夹自动转标签页**：新打开的 File Explorer 窗口自动转为已有窗口中的标签页
- **去重**：路径已打开时切换到现有标签，避免重复标签
- **保留选中项**：正确处理“在文件夹中显示”等操作传入的选中文件
- **轻量常驻**：只驻留系统托盘，不运行后台服务或常驻独立窗口
- **安全更新**：下载包必须匹配 GitHub SHA-256，替换前在目标目录完成校验
- **失败可恢复**：目标标签页确认成功后才关闭源窗口；更新失败时恢复旧版本

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
`artifacts\E-Tab-<版本>-win64.exe` 是可直接运行的测试文件，无需解压。
同时生成 `.zip.sha256` 校验文件，可用于发布前或下载后的完整性核验。
如果已经执行过依赖恢复，可使用 `.\pack.ps1 -NoRestore` 跳过重复的在线检查。

运行回归测试：

```powershell
dotnet run --project tests\E-Tab.Tests\E-Tab.Tests.csproj -c Release
```

## 使用

- 运行后驻留系统托盘并自动开始工作。
- 右键托盘图标可以看到版本号和几个开关（勾选即表示已开启）：
  - **Start with Windows**：开机自动启动。
  - **Auto-merge new windows**：新窗口自动变成标签页；关掉后新窗口保持独立，需要时用下面的命令合并。
  - **Merge all windows (Ctrl+Shift+E)**：把当前打开的资源管理器窗口合并到最前面的那个窗口。
  - **Check for updates...**：按需检查 GitHub 上的最新正式版；确认后自动下载、校验、替换并重启。
  - **Exit**：退出。
- 合并过程不会改动窗口的大小、位置和最大化状态；目标标签页验证失败时会保留并重新显示源窗口。
- 日志位于 `%LOCALAPPDATA%\E-Tab\logs\E-Tab.log`，达到 1 MB 后保留最近三份历史日志。

## 许可证

MIT，保留上游 [ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility) 的版权声明，见 [LICENSE](LICENSE)。
