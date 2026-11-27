# KTLanDrop

轻量级 Windows 电脑 ↔ iPad / 手机局域网互传工具（单文件 `.exe`，零第三方依赖）。

## 功能特性

- **文件双向互传**：
  - 电脑 → iPad：在 Safari 浏览器中浏览电脑共享目录、实时搜索、预览或下载文件。
  - iPad → 电脑：从 iPad 相册或「文件」App 直接上传图片/文档到电脑接收目录。
- **文字双向互传（选择性复制）**：
  - 电脑端与网页端实时聊天框，支持各自一键复制，**不会自动覆盖电脑剪贴板**。
- **自动识别局域网 IP + 二维码**：
  - 自动过滤虚拟网卡（如 Clash/Meta、VMware、Hyper-V），生成二维码，iPad 相机一扫即开。
- **自适应端口 & 一键防火墙**：
  - 端口被占用时自动顺延尝试可用端口，支持界面自定义端口；提供「一键放行防火墙」按钮（仅放行局域网 `LocalSubnet`）。
- **记忆配置**：
  - 自动将共享目录、接收目录与端口保存在 `%LocalAppData%\KTLanDrop\settings.ini`。

## 使用与编译

1. **直接下载**：前往本仓库的 **Actions**（或 **Releases**）页面下载编译好的 `KTLanDrop.exe` 双击运行。
2. **本地编译**：在任意 Windows 10/11 电脑上直接双击运行 `build.bat`（调用系统自带 `.NET Framework 4.x` 编译器 `csc.exe`，无需安装 Visual Studio），即可生成单文件 `KTLanDrop.exe`。
