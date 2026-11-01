# Moving your settings from an old build

[中文](#从旧版本迁移设置) · [English](#moving-your-settings-from-an-old-build-1)

## 从旧版本迁移设置

### 适用情况

早期的 HD2 Mod Quick Deploy（0.5.0 及更早，包括他人自行编译后传播的版本）把设置保存在另一个文件夹中。0.6.0 起改为现在的位置，新版本不会读取旧文件夹，因此打开新版本后 mod 库是空的。

| | 旧版本（0.5.0 及更早） | 新版本（0.6.0 起） |
|---|---|---|
| 设置文件夹 | `%APPDATA%\hd2-mod-cli` | `%USERPROFILE%\.hd2-mod-cli` |

只要旧文件夹存在且其中有 `library.json`，就可以按下面的步骤把 mod 库、加载顺序、开关、可选内容、标签和部署记录一起迁移到新版本。mod 文件本身不需要移动。

### 步骤

1. **关闭程序**：关闭所有 HD2 Mod Quick Deploy 窗口；如果正在使用命令行 `hd2qd`，也请先结束。
2. **打开旧文件夹**：在资源管理器的地址栏中输入 `%APPDATA%\hd2-mod-cli`，按回车。能看到 `library.json`、`deployed.json` 等文件，说明这里就是旧版本的设置。
3. **打开新文件夹**：在地址栏中输入 `%USERPROFILE%\.hd2-mod-cli`，按回车。
   - 提示找不到文件夹：先运行一次新版本再关闭，它会自动创建该文件夹，然后重新打开。
   - 已经在新版本中添加过 mod：先把这个文件夹中的全部内容复制到别处备份，下一步会替换其中的文件。
4. **复制**：选中旧文件夹中的全部内容（所有文件和子文件夹），复制到新文件夹中。出现同名文件时，选择“替换目标中的文件”。
5. **检查**：打开新版本，确认 mod 列表、顺序和开关与旧版本一致。窗口底部显示“游戏目录与 mod 库同步”时，说明部署记录也已迁移，无需重新部署。

### 注意

- **保留旧文件夹**：旧版本通过拖入 zip 安装的 mod 可能解压在旧文件夹的 `mods` 子文件夹中，mod 库仍然从那里读取这些 mod。迁移后请不要删除旧文件夹。
- **不要交替使用新旧版本**：两者各用各的设置文件夹，在一个版本中的更改不会出现在另一个版本中。迁移后请只使用新版本。
- **请从官方渠道下载**：https://github.com/pat-pat-patrick/hd2-mod-cli/releases

## Moving your settings from an old build

### Who this is for

Early builds of HD2 Mod Quick Deploy (0.5.0 and earlier, including copies other people compiled and passed around) kept their settings in another folder. Since 0.6.0 they are kept in the folder below, and a new build does not read the old one, so its mod library starts out empty.

| | Old builds (0.5.0 and earlier) | New builds (0.6.0 and later) |
|---|---|---|
| Settings folder | `%APPDATA%\hd2-mod-cli` | `%USERPROFILE%\.hd2-mod-cli` |

If the old folder exists and holds a `library.json`, the steps below bring your library, load order, on/off states, options, labels and deploy record over to the new build. The mod files themselves stay where they are.

### Steps

1. **Close the program**: close every HD2 Mod Quick Deploy window, and stop the `hd2qd` command line if you use it.
2. **Open the old folder**: type `%APPDATA%\hd2-mod-cli` in File Explorer's address bar and press Enter. If you see `library.json`, `deployed.json` and so on, these are the old build's settings.
3. **Open the new folder**: type `%USERPROFILE%\.hd2-mod-cli` in the address bar and press Enter.
   - If the folder cannot be found: start the new build once and close it; it creates the folder. Then open it again.
   - If you have already added mods in the new build: copy everything in this folder somewhere else first, as the next step replaces its files.
4. **Copy**: select everything in the old folder (all files and subfolders) and copy it into the new folder. When asked about files with the same name, choose **Replace the files in the destination**.
5. **Check**: start the new build and check that the mod list, its order and on/off states match the old build. When the bottom line reads "the game folder matches the library", the deploy record came over too and no new deploy is needed.

### Good to know

- **Keep the old folder**: mods an old build installed from a dropped zip may have been unpacked into the old folder's `mods` subfolder, and the library still reads them from there. Do not delete the old folder after moving.
- **Do not switch between old and new builds**: each keeps its own settings folder, so a change in one does not show in the other. After moving, use the new build only.
- **Download from the official page**: https://github.com/pat-pat-patrick/hd2-mod-cli/releases
