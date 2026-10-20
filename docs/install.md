# 安装与分发

本文面向两类人：

- **使用者**：想直接下载安装包，把 LangCode 装进 Visual Studio；
- **维护者**：想自己打包 VSIX，并发布到 Gitee 发行版。

> 不需要下载源码，也不需要装 .NET SDK。只要有一个 Visual Studio 2022，双击 `.vsix` 就能装好。

## 下载渠道

| 渠道             | 地址                                                                   | 说明                                           |
| ---------------- | ---------------------------------------------------------------------- | ---------------------------------------------- |
| 仓库直链（推荐） | <https://gitee.com/li_qi_lang/lang-code/raw/master/dist/LangCode.vsix> | 免登录直接下载，始终指向 `master` 上的最新构建 |
| Gitee 发行版     | <https://gitee.com/li_qi_lang/lang-code/releases>                      | 正式版本归档，适合固定版本、回滚与企业分发     |
| 从源码构建       | `scripts\package.cmd`                                                  | 开发者自行打包，产物在 `dist\`                 |

安装包文件名：

- `LangCode.vsix`：稳定文件名，方便固定链接；
- `LangCode-<版本>.vsix`：带版本号的副本，便于回滚与归档；
- `SHA256SUMS.txt`：安装包校验值。

## 安装前检查

| 项目 | 要求                                                            |
| ---- | --------------------------------------------------------------- |
| 系统 | Windows 10 / 11（64 位）                                        |
| IDE  | Visual Studio 2022（17.0 及以上），Community / Pro / Enterprise |
| 框架 | .NET Framework 4.7.2 及以上（Windows 10/11 通常已自带 4.8）     |
| 其他 | 安装前请**关闭所有 Visual Studio 窗口**                         |

> LangCode 只提供 `amd64` 架构的安装目标，不支持 32 位 Visual Studio（VS 2022 本身也只有 64 位）。

## 安装

### 双击安装（推荐）

1. 从仓库直链或 Gitee 发行版下载 `LangCode.vsix`；
2. 关闭 Visual Studio；
3. 双击 `.vsix` 文件；
4. 在 VSIX Installer 窗口中勾选要安装的 Visual Studio 2022 实例，点 **Install**；
5. 安装完成后启动 Visual Studio；
6. 打开 **扩展 → 管理扩展 → 已安装**，确认 **LangCode** 已启用。

### 命令行安装（静默 / 批量）

适合脚本化安装或企业分发。先找到 `VSIXInstaller.exe`，常见位置：

```text
C:\Program Files (x86)\Microsoft Visual Studio\Installer\vsixinstaller.exe
C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe
```

静默安装：

```cmd
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vsixinstaller.exe" /quiet /norestart "D:\downloads\LangCode.vsix"
```

为所有用户安装（需要管理员权限）：

```cmd
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vsixinstaller.exe" /admin /quiet /norestart "D:\downloads\LangCode.vsix"
```

安装到 Visual Studio 实验实例（调试扩展时用）：

```cmd
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vsixinstaller.exe" /rootSuffix:Exp "D:\downloads\LangCode.vsix"
```

> 不建议手动把 VSIX 解压后复制进 `Common7\IDE\Extensions\`。这样不会正确注册 `.pkgdef`，容易出现「扩展列表里有、菜单却打不开」的问题。

## 升级

1. 关闭 Visual Studio；
2. 下载新版本的 `LangCode.vsix`；
3. 双击安装，向导会识别出已安装的旧版本，确认 **Update** 即可。

升级不会删除你的设置和历史会话，它们保存在 `%APPDATA%\LangCode\vsext\`。想回滚时，下载旧版本 VSIX 覆盖安装即可。

## 卸载

**图形界面**：**扩展 → 管理扩展 → 已安装** → 找到 **LangCode** → **卸载**，然后按提示重启 Visual Studio。

**命令行**：

```cmd
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vsixinstaller.exe" /uninstall:LangCode.4f6c1b2e-9a3d-4c7e-8b21-5d0e6f7a8b9c
```

卸载扩展不会删除本地数据。需要彻底清理时，再手动删除：

```text
%APPDATA%\LangCode\vsext\
```

## 首次配置

1. 菜单 **工具 → 打开 LangCode**（也可以在代码窗口右键 → **LangCode**）；
2. 在 **设置** 里选择 API 提供商，填入 API Key（本地模型可留空）；
3. 选择一个模型，或直接输入模型名；
4. 打开一个解决方案 / 文件夹，在输入框里描述需求，回车开始。

## 校验安装包

下载后可以核对 SHA256，确认文件没有被篡改或下载不完整：

```cmd
certutil -hashfile LangCode.vsix SHA256
```

把输出与 [`dist/SHA256SUMS.txt`](../dist/SHA256SUMS.txt) 中的值比对即可（忽略大小写与空格）。

## 常见问题

| 现象                                     | 处理                                                                                                           |
| ---------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| 双击 `.vsix` 被解压软件打开              | 右键 → 打开方式 → **Visual Studio Version Selector**，或用上面的命令行方式安装                                 |
| 提示「此扩展无法安装到任何已安装的产品」 | 确认安装的是 64 位 Visual Studio 2022（17.0+），且已安装 **.NET 桌面开发** 相关工作负载                        |
| 浏览器 / Windows 提示文件不安全          | 右键文件 → **属性** → 勾选 **解除锁定** → 确定，再安装                                                         |
| 安装后菜单里找不到 LangCode              | 到 **管理扩展 → 已安装** 确认已启用；完全退出并重启 VS；仍无菜单时用 `devenv /log` 查看 `ActivityLog.xml`      |
| 扩展加载失败                             | 检查 .NET Framework 版本（需 4.7.2+）；查看 `%APPDATA%\Microsoft\VisualStudio\17.0_*\ActivityLog.xml` 中的错误 |
| 想同时保留稳定版和测试版                 | 把测试版装到实验实例：`/rootSuffix:Exp`，用 `devenv /rootsuffix Exp` 启动                                      |
| 升级后设置还在吗                         | 在。设置、历史、检查点都在 `%APPDATA%\LangCode\vsext\`，升级不清理                                             |
| 公司电脑不能联网安装                     | 用离线 VSIX：内网共享 `LangCode.vsix`，通过 `VSIXInstaller.exe /quiet` 静默安装                                |

## 维护者：打包与发布到 Gitee

### 1. 调整版本号

发新版前，保证以下两处版本一致：

- [`LangCode/source.extension.vsixmanifest`](../LangCode/source.extension.vsixmanifest)：`Identity@Version`
- [`LangCode/Properties/AssemblyInfo.cs`](../LangCode/Properties/AssemblyInfo.cs)：`AssemblyVersion` / `AssemblyFileVersion` / `AssemblyInformationalVersion`

### 2. 一键打包

```cmd
scripts\package.cmd
```

脚本会执行：

1. `dotnet restore LangCode.sln`
2. `dotnet build LangCode/LangCode.csproj -c Release`
3. 把生成的 VSIX 复制为 `dist\LangCode.vsix` 与 `dist\LangCode-<版本>.vsix`
4. 生成 `dist\SHA256SUMS.txt`

也可以手动构建：

```cmd
dotnet restore LangCode.sln
dotnet build LangCode/LangCode.csproj -c Release
```

产物位置：`LangCode\bin\Release\net472\LangCode.vsix`。

### 3. 提交安装包

`dist\` 下的 VSIX 是**故意入库**的，`.gitignore` 里已单独放行，这样用户不登录 Gitee 也能从仓库直链下载：

```cmd
git add dist\LangCode.vsix dist\LangCode-<版本>.vsix dist\SHA256SUMS.txt
git add README.md docs\install.md scripts .gitignore LangCode
git commit -m "chore: 发布 v<版本> 安装包"
git push origin master
```

### 4. 发布到 Gitee 发行版

**方式一：网页手动上传（最直观）**

1. 打开 <https://gitee.com/li_qi_lang/lang-code/releases/new>；
2. 填写 tag（例如 `v1.0.0`）、发行版标题与说明；
3. 上传 `dist\LangCode.vsix` 与 `dist\SHA256SUMS.txt` 作为附件；
4. 点击 **发布**。

**方式二：脚本自动上传**

1. 在 <https://gitee.com/profile/personal_access_tokens> 生成私人令牌，勾选 `projects` 权限；
2. 设置环境变量并运行脚本：

```cmd
set GITEE_TOKEN=你的令牌
powershell -ExecutionPolicy Bypass -File scripts\publish-gitee.ps1
```

脚本会读取 VSIX 清单里的版本号作为 tag（如 `v1.0.0`），创建发行版并上传 `dist\` 下的安装包与校验文件。若同名发行版已存在，会复用该发行版继续上传附件。

上传成功后的下载地址形如：

```text
https://gitee.com/li_qi_lang/lang-code/releases/download/v1.0.0/LangCode.vsix
```

### 5. 发布后自检

- 在未登录 Gitee 的浏览器里打开发行版下载链接，确认能直接下载；
- 核对下载文件的 SHA256 与 `SHA256SUMS.txt` 一致；
- 在一台干净的 Visual Studio 2022 上双击安装，确认菜单 **工具 → 打开 LangCode** 可用；
- 确认 **扩展 → 管理扩展 → 已安装** 中的图标、版本号与 `MoreInfoUrl` 指向 Gitee。
