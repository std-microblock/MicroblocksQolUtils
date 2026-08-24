# microblock's QoL Utils

[English](README.en.md)

面向 Celeste + Everest 的 QoL 工具模组。项目以 Windows 为主要目标平台，
但字体、图标和界面绘制所需的 native rasterizer 也可以构建到 Linux/macOS。
MiaoNet、CollabUtils2 和 SpeedrunTool 都是运行时可选集成，不是硬依赖。

## 功能

### 界面与系统

- Material You 风格的 HUD 信息卡、设置页、章节选择页和 Everest 模组设置页。
  HUD 背景、亚克力模糊和模组设置页替换都可以单独开关。
- 自绘的 QOL 设置页：按分类浏览设置、搜索设置、鼠标/键盘/手柄操作，
  并保留 Everest 原生的绑定配置和自定义设置项。
- 可选替换原版选关页：支持最近游玩、地图集、搜索、作者/描述/标签预览，
  键盘、手柄、鼠标和滚轮操作，并通过原生 OuiChapterPanel 流程进入章节。
  会遵守官方地图解锁限制。
- CollabUtils2 集成是反射式的：默认隐藏 Collab 地图和 Gym；打开高级选项后
  可以显示它们。多大厅 Collab 会按 Lobby 分组，并可以折叠整个 Lobby。
- 内置 Material Symbols 图标和跨平台字体渲染。文本按实际像素栅格化并做缓存，
  默认字体是 Microsoft YaHei UI，可以从已安装字体中选择。
- Windows HiDPI 支持、输入法自动切换（文本输入使用中文布局，正常游戏使用英文布局）。
- 可选移除房间过渡和死亡动画；碰撞箱支持隐藏、叠加显示、仅显示碰撞箱三种模式。
- 暂停菜单中提供“Microblock 的 QOL 工具”入口。

### HUD 与小地图

- 滚动 FPS、CPU 帧耗时，以及在 Motion Smoothing 可用时分别显示物理帧率和渲染帧率。
- 可选帧卡顿提示和帧分析 HUD。
- 基于当前固体网格绘制的圆形/方形小地图：
  - 可调整尺寸、缩放和键盘/手柄缩放按键；
  - 房间边界、房间背景、背景透明度和自适应地图颜色；
  - 房间之间到地图终点的缓存最短路线与剩余房间数；
  - 草莓、金草莓、月莓、心、磁带、钥匙和宝石标记；
  - 根据存档显示收集状态；可选在边缘显示附近房间的草莓。
- 可选显示 MiaoNet 同地图玩家、头像、越界玩家和玩家名称；玩家名称可以设置为
  不显示、仅显示关注玩家或显示所有人。
- 可隐藏 MiaoNet 原生的越界名字标签。

### MiaoNet 关注与通知

MiaoNet 存在时，模组会通过反射读取同地图玩家的位置、房间、名称和头像，
MiaoNet 不存在时不会阻止模组加载。

Everest 控制台命令：

~~~text
qol_watch <玩家名>
qol_unwatch <玩家名>
qol_watch_list
~~~

MiaoNet 聊天框还注册了 /qol（别名 /mu）：

~~~text
/qol watch <玩家名>
/qol unwatch <玩家名>
/qol list
~~~

Windows 下，关注的玩家换房间且 Celeste 不在前台时，会发送系统通知。

### 录制与死亡回放

录制功能支持 Windows、Linux 和 macOS native backend。Windows 使用 WGC，macOS
使用 ScreenCaptureKit，Linux 使用 xdg-desktop-portal/PipeWire；不会启动 ffmpeg
可执行文件，也不会使用托管帧缓冲或子进程。

macOS 首次使用时需要授予 Celeste“屏幕与系统录音”权限。Linux 会显示桌面门户的
共享选择器，请选择 Celeste 窗口或其所在屏幕；Wayland 和支持 PipeWire 的 X11
桌面都走同一套门户接口。

- 自动录制策略：每个房间都录制，或只录制携带金草莓的 run。
- 控制台和设置页都支持手动录制、保存和丢弃。
- 完整录像和死亡回放使用独立的捕获会话；死亡回放默认保留最近 30 秒，
  可设置为 10–60 秒，并在死亡后自动保存、复活后继续录制。
- 连续录制只保留成功片段。死亡、房间切换、暂停、SpeedrunTool 加载和自定义
  respawn 点会改变最终剪辑时间线，不会让失败过程进入最终视频。
- 房间完成后，native finalizer 只读取保留的片段，生成无间隙的 MP4；
  支持进度显示、完整录像/死亡回放两个录像库、打开文件夹、播放和删除。
- 输出默认位于 %USERPROFILE%\Videos\Celeste\microblocks-qol-recordings，
  也可以在设置中指定目录。录像分别放在 full/<区域> 和 deaths/<区域>；
  每个完成的 MP4 旁边会有 .timeline.json 时间线文件。
- 视频优先使用平台 H.264 编码器（Windows Media Foundation、macOS
  VideoToolbox）；Linux 在没有可直接使用的 H.264 编码器时回退到 MP4 中的
  MPEG-4 Part 2。音频使用 AAC。可以选择录制 UI 音效、帧率、码率和编码器，
  并设置完整录像/死亡回放的保留数量或立即清理旧录像。
- 音频通过 FMOD DSP tap 采集 gameplay_sfx、music 和可选的 ui_sfx。
  音频块写入 .sfxchunks sidecar，最终化时按视频剪辑时间线混音，不把整段音频
  堆在内存中。
- BGM 可以直接使用捕获的游戏混音，也可以使用 SfxOnlyWithPostMix：
  该模式按事件、循环、跳转等时间线断点切分音乐；如果配置了干净 BGM 映射，
  则用映射文件替换对应事件片段。

可选的 BgmEventMapFile 是一个 JSON 对象，路径相对 JSON 文件所在目录解析：

~~~json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
~~~

没有映射时仍会使用游戏捕获的音乐，并按照相同的死亡、重生点和
SpeedrunTool 时间线进行裁剪。

### Profiler

设置页中的 Profiler 可以启动一次 10 秒的进程内 EventPipe 栈采样：

- 按 Update 和 Render 分开统计；
- 显示独占 CPU 时间、占比、所属模组程序集和 MonoMod hook 目标；
- 支持“简单·仅 Mod”和“专业·全部”两种列表；
- 生成 CSV 和 .nettrace，保存到
  %LOCALAPPDATA%\MicroblocksQolUtils\profiles；
- 轻量级的帧耗时 HUD 不依赖完整采样。

## 控制台命令

除上面的关注命令外，录制和 native capture 还提供：

~~~text
qol_capture_probe_start
qol_capture_probe_stats
qol_capture_probe_stop

qol_record_start
qol_record_save
qol_record_discard
qol_record_status
~~~

前三个命令用于开发时检查平台 scap 捕获、队列深度、丢帧和媒体时长，
不会自动开启正常录制。

## 构建与安装

默认 Celeste 路径是：

~~~text
C:\SteamLibrary\steamapps\common\Celeste
~~~

需要 Node.js、.NET 8 SDK、Rust，以及对应平台的 C/C++ 工具链。
Windows 下如果要构建完整录制后端，还需要：

- Visual Studio C++ Build Tools；
- LLVM/Clang（包括 libclang.dll 和 clang-cl.exe）；
- MSYS2、GNU make、Perl 和 NASM；
- tar。

Linux 完整构建还需要 Clang/libclang、GNU make、pkg-config、PipeWire 和 D-Bus
开发包；macOS 完整构建需要 Xcode Command Line Tools。各平台都会从已校验的
FFmpeg 8.1 源码构建并随包附带最小 LGPL shared runtime。

仓库的入口脚本会构建 managed mod、native rasterizer、当前平台的
capture/recording backend，并生成 Build 和 MicroblocksQolUtils.zip：

~~~powershell
node scripts/build-qol-mod.mjs
~~~

构建并安装到默认 Celeste 安装目录：

~~~powershell
node scripts/build-qol-mod.mjs --install
~~~

如果游戏不在默认路径，设置 CELESTE_ROOT。安装前请关闭 Celeste，脚本会拒绝
替换正在被游戏加载的 DLL：

~~~powershell
$env:CELESTE_ROOT = "D:\Games\Celeste"
node scripts/build-qol-mod.mjs --install
~~~

可以显式选择 CI 使用的平台：

~~~powershell
node scripts/build-qol-mod.mjs --target x86_64-pc-windows-msvc
node scripts/build-qol-mod.mjs --target x86_64-unknown-linux-gnu
node scripts/build-qol-mod.mjs --target x86_64-apple-darwin
~~~

Windows、Linux 和 macOS 包都包含录制后端及其 FFmpeg shared runtime；只有
Windows 系统通知仍是平台专用功能。运行时不会打包或调用 ffmpeg 可执行文件。

GitHub Actions 会运行 Rust 格式检查和测试，并构建 Windows x64、Linux x64
和 macOS x64 包。master 的每个提交会更新 nightly 预发布，v* 标签会发布相同的
三个平台构建产物。

## 依赖说明

- MiaoNet、CollabUtils2、SpeedrunTool：运行时可选，使用反射或桥接接口。
- Material Symbols：仓库内嵌图标资源。
- third_party/scap：固定版本并带本地补丁的捕获依赖。
