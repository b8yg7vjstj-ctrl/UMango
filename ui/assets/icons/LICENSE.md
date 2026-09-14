# 图标来源与授权

`npm.svg` / `pnpm.svg` / `git.svg` / `homebrew.svg` 取自 **simple-icons**
（https://github.com/simple-icons/simple-icons），授权为 **CC0 1.0 Universal**
（公有领域奉献，允许商用与修改，无需署名）。此处保留本说明仅为溯源。

## winget 与 scoop 的缺失说明

simple-icons 中没有 winget 与 scoop 的品牌图标，且本项目开发环境无法访问
github.com 获取其官方 logo 资源。因此这两个包源在界面上使用**自绘字母徽章**
（品牌主色圆角方块 + 字母），而不是官方 logo —— 宁可样式朴素，
也不要放一个来源不明或画错的商标图形。

如需替换为官方 logo：把 `winget.svg` / `scoop.svg` 放入本目录，
并在 `lib/widgets/source_icon.dart` 的映射表中登记即可，无需改动其他代码。
