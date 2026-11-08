# 第三方组件

本仓库发布管理工具的源码及其构建结果，不包含第三方 GPU 运行时、ReShade、PotPlayer 或 mpv 二进制。

| 组件 | 固定版本/来源 | 使用方式 |
| --- | --- | --- |
| NR Nexus | [v0.2.2](https://github.com/williamstosh/RenoDX-DLSSNR-Nexus/releases/tag/v0.2.2)，提交 `1e725d07` | 按固定包下载；保留包内 LICENSE、THIRD_PARTY_NOTICES 及其他许可文件 |
| ReShade | [6.8.0 Add-on](https://reshade.me/) | 仅作为数据提取 64 位加载组件，不执行安装器 |
| 社区 NR 运行时 | [310.9.16](https://github.com/RankFTW/rhi-repo/releases/tag/dlssnr-310.9.16) | 下载并核验哈希；适用 NVIDIA 及包内各项使用条款 |
| DLSS SR 运行时 | [310.9.16](https://github.com/RankFTW/rhi-repo/releases/tag/dlss-310.9.16) | 仅在安装规则允许时放置，保留游戏原有 Streamline 文件 |
| mpv | [55d3feb45 CI 构建](https://github.com/mpv-player/mpv/tree/55d3feb45) | 可选准备脚本下载；保留 LICENSE.GPL；完整来源及校验值见 tools/mpv-SOURCE.txt |
| PotPlayer | [Kakao 官方网站](https://potplayer.daum.net/) | 用户自行安装，使用原装播放器和原生播放列表 |

NR 包的 SHA-256 与下载来源固定在 `src/NeuralPackages.cs`。准备 NR 时保留原始压缩包和包内全部相关许可、声明及说明，用于后续验证。MIT 授权仅覆盖本项目自己的代码，不改变上述组件的权利或适用条款。

