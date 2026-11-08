# 游戏 1–6 倍补帧

用户明确选择“先直接实现游戏补帧，技术细节由你安排”；保留游戏与视频 NR、中文界面、主页两个按钮。本轮视频和网页不增加插帧。1× 表示撤回本工具的补帧，2×–6× 为请求的总输出倍率，不能以 NR 层数、重复呈现计数或 UI 倍率冒充实际插帧。

采用 abc354402600/OptiScaler-Aurora 的固定发行 aurora-v1.1.2，源码提交 1d22875d05fac2d7ffb86ed912b943ce1a6e915a。原始 7z 为 219575034 字节，SHA-256 为 17c608e1a848505ab72dea9ab0e3dce5e192072757189a73bc3fd87fd6e88af8。只作数据解包，不运行 setup、OptiPatcher 或 runtime_sync。仅部署实际需要的固定加载库与 GPL 许可/来源声明；不复制整个游戏运行库集合。

首个明确支持范围为 x64 DX12 游戏的原生 Streamline DLSS 帧生成：游戏已有 Streamline >= 2.7.1、适用的 DLSSG 310.9 运行时，并静态导入 WINMM.dll。使用独立 winmm.dll 代理，现有 ReShade dxgi.dll 保持原状。不能验证该输入路径时拒绝补帧并解释原因，不静默降成 2× 或覆盖其他 mod。不会替换游戏官方 DLL、改驱动/系统设置或绕过反作弊。

配置原生 FG 输入/输出为 nofg，保留游戏原生 FG，由 [DLSSG] OverrideInterpolationCount=倍率-1、AdaMfgUnlock=true、AdaBlackwellKernels=true 请求 RTX40 MFG。该实验路径涉及上游内存中的硬件兼容补丁，缺少 Blackwell Flip Metering，不能承诺均匀帧时间或原生官方支持。禁用插件自动下载/OTA、额外 ASI/SpecialK/ReShade 加载及 Aurora NR，避免与已有 Nexus 双重 NR。游戏内仍需开启 DLSS 帧生成；界面直接说明这一要求。

选择单个游戏后显示中文设置窗：1×–6×，默认 1×，一次点击加载并启动；视频流程不显示这些选项。游戏设置保存在独立 FG 配置中，修改倍率保留未知配置。主界面仍只有选择和全部取消。所有安装采用独立 FG 所有权日志；取消合并 NR/FG 目标，运行中等退出后撤回，外部修改文件保留。

读取了用户网盘目录：DLSS5-PLUS v1.1.1、Lecram、SF、GITC、Feeder 等。没有运行其安装器、访问个人文件或将未知插件混入当前安装；参考仅用于功能边界，后端取可审计固定来源。

验证包括离线反例/回归、真实固定包哈希/PE/许可验证、两按钮与游戏倍率窗渲染、实际加载与配置日志。仅在本次实际日志提供成功 MFG 路径/倍率证据时报告对应状态；未做逐帧独立画面验收时明示尚未验证不同生成帧和全部倍率。现有燕云渲染目录已只读确认 WINMM 静态导入、SL 2.14.16、DLSSG 310.9.16。
