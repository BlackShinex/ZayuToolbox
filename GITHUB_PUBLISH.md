# 把这个项目发布到 GitHub

项目已经整理成适合 GitHub 的仓库结构：`.gitignore`、`.gitattributes`、`.editorconfig`、GitHub Actions、README、CHANGELOG、MIT LICENSE 和界面截图都已就绪。

## 0. 需要准备

- 一个 GitHub 账号
- Git for Windows：<https://git-scm.com/download/win>
- 项目目录：`C:\Users\black\Documents\deepseek-harness\default-workspace\ip-info`

安装 Git 后打开 PowerShell 验证：

```powershell
git --version
```

## 1. 在 GitHub 创建仓库

1. 打开 <https://github.com/new>；
2. Repository name 填 `IpInfo`（或你喜欢的名字）；
3. 选择 Public / Private；
4. **不要**勾选 “Add a README file”“Add .gitignore”“Choose a license”，因为本地已经有这些文件；
5. 点击 Create repository。

## 2. 本地初始化并提交

在 PowerShell 中执行（把路径换成你实际存放项目、且包含 `IpInfo.sln` 的目录）：

```powershell
cd <项目目录>

git init
git config user.name "BlackCat"
git config user.email "你的GitHub邮箱"

git add .
git status
```

确认 `git status` 中**没有** `bin/`、`obj/`、`dist/`（`.gitignore` 已忽略），然后提交：

```powershell
git commit -m "Initial commit: 杂鱼串流工具箱 v1.5"
git branch -M main
```

## 3. 关联 GitHub 并推送

把 `<你的GitHub用户名>` 替换成实际用户名：

```powershell
git remote add origin https://github.com/<你的GitHub用户名>/ZayuToolbox.git
git push -u origin main
```

第一次推送会弹出登录窗口，选择 **Sign in with your browser** 登录 GitHub 即可。  
如果使用 PAT（Personal Access Token），把上面的 URL 换成：

```text
https://<你的用户名>:<你的PAT>@github.com/<你的用户名>/ZayuToolbox.git
```

> 如果创建仓库时误勾选了 README，先执行 `git pull --rebase origin main` 再推送。

### 方式 B：使用 GitHub Desktop（不想敲命令时）

1. 安装 GitHub Desktop 并登录；
2. **File → Add local repository**，选择本项目目录；
3. 如果提示 “not a Git repository”，点击 **create a repository**；
4. 提交所有文件（确认没有 `bin/obj/dist`）；
5. 点击 **Publish repository**，选择 Public / Private 后发布。

## 4. 创建 Release（发布可直接下载的程序）

本地已经生成了发布包：

```text
dist\ZayuToolbox-1.5-win-x64.zip
```

在 GitHub 仓库页面：

1. 点击右侧 **Releases** → **Draft a new release**；
2. Tag 填 `v1.5`，Target 选 `main`；
3. 标题填 `杂鱼串流工具箱 v1.5`；
4. 把 `dist\ZayuToolbox-1.5-win-x64.zip` 拖到附件区域；
5. 填写更新说明（可以把 `CHANGELOG.md` 中 v1.5 的内容复制进去）；
6. 点击 **Publish release**。

之后别人就可以从 Releases 下载并直接运行 `杂鱼串流工具箱-1.5\杂鱼串流工具箱.exe`。

## 5. GitHub Actions

提交后，仓库会自动运行 `.github/workflows/build.yml`：

- 使用 Windows Runner 构建 Release；
- 运行离线核心测试；
- 生成 `IpInfo-win-x64` artifact（可在 Actions 页面下载）。

首次运行如果提示权限/构建问题，可在仓库 **Settings → Actions → General** 确认 Workflow permissions 为 Read and write（当前工作流只需要读取和 artifact，默认 Read 即可）。

## 6. 发布后建议补充

- 在仓库 About 区域填写：`Windows / WPF / IPv4 / IPv6 / NAT / STUN` 等 Topics；
- 在 README 顶部加上仓库地址和 Releases 链接；
- 如果需要，在 README 增加 GitHub Actions 构建徽章；
- 如果不想使用 MIT 协议，可删除 `LICENSE` 或替换成其他协议后再提交。

## 注意

- 不要把 `dist/`、`bin/`、`obj/` 提交进仓库；`dist` 里的 zip 作为 GitHub Release 附件即可。
- 截图 `docs/ui-preview-full.png` 中的 IP 已经默认打码；提交前仍建议自己检查一遍 `git status` 和截图内容。
- 本项目的编译只需要 Windows 自带的 .NET Framework 4.8 + MSBuild，不需要 .NET SDK。
