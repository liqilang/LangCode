# 安装与分发

LangCode 以 `LangCode.vsix` 安装包分发，不需要下载源码。

## 下载

<https://gitee.com/li_qi_lang/lang-code/raw/master/dist/LangCode.vsix>

## 环境要求

| 项目 | 要求                              |
| ---- | --------------------------------- |
| 系统 | Windows 10 / 11（64 位）          |
| IDE  | Visual Studio 2022（17.0 及以上） |
| 框架 | .NET Framework 4.7.2 及以上       |

## 安装

1. 关闭所有 Visual Studio 窗口；
2. 双击下载好的 `LangCode.vsix`；
3. 在安装窗口中勾选 Visual Studio 2022，点击 **Install**；
4. 启动 Visual Studio，打开 **工具 → 打开 LangCode**。

安装完成后，可在 **扩展 → 管理扩展 → 已安装** 中确认 LangCode 已启用。

## 升级

下载新版 `LangCode.vsix` 后双击覆盖安装即可。设置和历史记录都会保留。

## 卸载

打开 **扩展 → 管理扩展 → 已安装**，找到 **LangCode**，点击 **卸载**，然后重启 Visual Studio。

本地数据保存在 `%APPDATA%\LangCode\vsext\`，卸载扩展不会删除它；需要彻底清理时再手动删除该目录。

## 首次配置

1. 打开 **工具 → 打开 LangCode**；
2. 在 **设置** 里选择 API 提供商，填入 API Key（本地模型可留空）；
3. 选择一个模型，或直接输入模型名；
4. 打开一个解决方案 / 文件夹，在输入框里描述需求，回车开始。

## 常见问题

| 现象                           | 处理                                                      |
| ------------------------------ | --------------------------------------------------------- |
| 双击 `.vsix` 被解压软件打开    | 右键 → 打开方式 → **Visual Studio Version Selector**      |
| 提示无法安装到任何已安装的产品 | 确认是 64 位 Visual Studio 2022（17.0 及以上）            |
| 浏览器提示文件不安全           | 右键文件 → **属性** → 勾选 **解除锁定** → 确定后再安装    |
| 安装后找不到 LangCode          | 到 **管理扩展 → 已安装** 确认已启用，并重启 Visual Studio |
