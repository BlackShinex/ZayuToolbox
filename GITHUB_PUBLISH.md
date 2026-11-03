# GitHub 发布说明

## 发布单个 exe

```powershell
.\tools\build.ps1 -Release -Package
```

产物：

```text
dist\杂鱼串流工具箱.exe
```

直接把 exe 上传到 GitHub Release 即可，不需要 zip。

## 创建 Release

1. 在仓库页面打开 **Releases → Draft a new release**；
2. Tag 填 `v1.8`，Target 选 `main`；
3. 标题填 `杂鱼串流工具箱 v1.8`；
4. 把 `dist\杂鱼串流工具箱.exe` 拖入附件；
5. 更新说明直接从 `RELEASE_NOTES.md` 复制；
6. 发布。

## 发布前检查

```powershell
.\tools\build.ps1 -Release -Tests -Package
.\tools\ui-test-background.ps1 -AppPath .\dist\杂鱼串流工具箱.exe
```

确认核心测试和完整后台 UI 测试均通过，再上传 exe。

## GitHub Actions

`.github/workflows/build.yml` 会自动构建、运行核心测试并上传单文件 exe artifact。
