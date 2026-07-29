# WinMusicPlayer

Windows 本地音乐播放器。C# + WPF (.NET 10)，与 [MacMusicPlayer](https://github.com/will-meet-s/Local_Music_Player_MAC)
功能对齐，另加**桌面歌词**。

## 功能

- 选择文件夹，递归扫描音乐（mp3 / m4a / aac / flac / wav / wma / aiff / alac）
- 手动刷新曲库：同步新增 / 删除的文件，不打断播放
- 播放、暂停、停止、上一首、下一首、拖动进度、音量
- 四种播放顺序：顺序播放 → 列表循环 → 单曲循环 → 随机
- 歌词：同名 `.lrc` 优先，其次读音频内嵌歌词；逐行高亮自动滚动，点某行可跳播
- **桌面歌词**：置顶浮层，可拖动、调字号与颜色、锁定后鼠标穿透
- 搜索（歌名 / 歌手 / 专辑）与排序（文件顺序 / 歌曲名 / 歌手名，可升降序）
- 无缝切歌（gapless）、音量归一化（ReplayGain）、独占输出（WASAPI Exclusive，可选）
- 右侧区域三种展示模式：封面 + 歌词 / 只看封面 / 只看歌词
- 亚克力半透明窗口背景，不透明度可调
- 托盘常驻：关掉主窗口后继续放歌
- 记住上次的文件夹、播放模式、音量、排序、布局、桌面歌词位置

## 环境要求

- Windows 10 1809+（FLAC / ALAC 解码依赖系统自带的 Media Foundation 解码器）
- .NET 10 SDK（开发）/ .NET 10 桌面运行时（运行）

## 构建运行

```powershell
dotnet restore
dotnet test                                     # 跑单元测试
dotnet run --project src\WinMusicPlayer          # 直接运行

# 发布成单个 exe（自带运行时，对方不用装 .NET）
dotnet publish src\WinMusicPlayer -p:PublishProfile=win-x64
```

产物是 `publish\win-x64\WinMusicPlayer.exe`，**只有这一个文件**，可以随意拷到桌面或别的机器。
参数都写在 `src\WinMusicPlayer\Properties\PublishProfiles\win-x64.pubxml` 里，其中
`IncludeNativeLibrariesForSelfExtract` 是关键 —— 少了它，WPF 的原生库会散落在 exe 旁边，
单独把 exe 拖走就打不开。

也可以直接用 Visual Studio 2022 打开 `WinMusicPlayer.sln`。

## 依赖

| 包 | 用途 |
|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 音频解码与 WASAPI 输出 |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | 标签读取：ID3v2 / Vorbis Comment / MP4 atom / APE |

读旧版中文 `.lrc` 需要的 GB18030 编码由框架自带的 `CodePagesEncodingProvider` 提供
（.NET 10 已内置，不再需要 `System.Text.Encoding.CodePages` 包引用）。

## 桌面歌词

在设置面板勾选「显示桌面歌词」，或右键托盘图标 →「桌面歌词」。

- 双行显示：当前行大字高亮，下一行小字暗一些
- **拖动**：鼠标按住任意位置拖走，位置会被记住
- **工具条**：鼠标移上去出现，可调字号（16–72）、切换 5 种配色、锁定、关闭
- **锁定**：开启鼠标穿透，点击直接落到底下的窗口，不会挡住桌面图标。
  锁定后浮层自己收不到点击了，**解锁要走托盘菜单**的「锁定桌面歌词」
- 文字带黑色外发光描边 —— 桌面壁纸明暗不定，没有它在浅色背景上会看不清
- 没有逐行歌词时退而显示曲名，不留空白

## 音频处理

### 无缝切歌（始终开启）

当前曲开始播放后立刻把下一首解码好挂进管线，播完直接接上。关键实现是
`GaplessSampleProvider.Read` **永远返回请求的全部长度**（不足处补静音）——
一旦返回 0，NAudio 就会停掉输出设备，之后续播必须重启设备，那就有空档了。

例外：**随机模式每轮的最后一次切歌不是无缝的**。下一轮的随机顺序要到真正翻页时才洗出来，
预加载阶段无从得知。

### 音量归一化（默认开启）

读 `REPLAYGAIN_TRACK_GAIN` / `REPLAYGAIN_TRACK_PEAK` 标签补偿响度差异。

- 只用**曲目级**增益，不用专辑级 —— 随机播放是常态，专辑级只在整张连听时才正确
- 增益挂在独立的 `VolumeSampleProvider` 上，不动设备主音量：后者是用户的音量旋钮，
  且上限为 1，无法为偏轻的曲目提升音量
- 已知峰值时保证补偿后不削波，增益系数钳制在 0.05–4 倍
- 没打标签的文件不受任何影响

### 独占输出（默认关闭）

WASAPI Exclusive 模式：绕过系统混音器，用文件原生采样率直推设备。

对应 macOS 版的「匹配输出采样率」。默认关闭，因为：

- 独占期间**其他程序发不出声**
- 与无缝播放冲突 —— 相邻曲目采样率不同时必须重建设备，那一次切歌有停顿
- 设备不支持所请求的独占格式时会自动退回共享模式并提示
- 只有接了像样的 DAC / 耳放才可能听出差别

## 搜索与排序

搜索匹配**歌名、歌手、专辑**，忽略大小写与音调符号（输 `cafe` 能搜到 `Café`）。

排序维度：文件顺序（默认，自然序，`track2` 排在 `track10` 前）、歌曲名、歌手名。
没有歌手信息的曲目在按歌手排序时始终垫底，正序倒序都一样。

排序和搜索会同时改变**播放顺序**。搜索状态下按下一首只在匹配结果里循环。
正在播的歌被过滤掉时**歌继续放**，只是列表里没有高亮项。

## 刷新曲库

**不会自动监听文件夹变化。** 点顶栏的 ↻ 按钮（托盘菜单里也有「刷新曲库」）。

| | 选择文件夹 | 刷新 |
|---|---|---|
| 播放 | 停止 | **继续，不打断** |
| 搜索词 / 排序 | 清空 / 保留 | 都保留 |
| 已读的元数据 | 全部重读 | 复用，只读新文件 |

### 正在播的歌被删了会怎样

歌**照常播完**（文件句柄已经打开）。刷新后列表里不再有它，右侧提示「文件已不在曲库中，
本曲仍可播完」。播完之后：

| 情况 | 行为 |
|---|---|
| 原序号仍在新列表范围内 | 从该序号继续 |
| 原序号超出新列表长度 | 等同播到结尾：顺序播放停止，循环 / 随机回第一首 |

## 与 macOS 版的差异

| macOS | Windows | 说明 |
|---|---|---|
| `NSVisualEffectView` 磨砂 | DWM 亚克力 | 需 Win11 22H2+；更早的系统退化为半透明纯色，功能不受影响 |
| 菜单栏状态项 | 系统托盘 | 用 WinForms `NotifyIcon`，右键菜单而非弹出面板 |
| CoreAudio 采样率匹配 | WASAPI 独占模式 | Windows 上的对应做法 |
| 自研 FLAC Vorbis Comment 解析 | 删除 | TagLib# 原生支持，不必手写 |
| `⌘Q` 等快捷键 | 删除 | macOS 版实测未生效，不移植 |
| `.icns` / `.dmg` | `.ico` / 单文件 exe | — |
| — | **桌面歌词** | Windows 版新增 |

## 代码结构

```
src/
  MusicCore/                    纯逻辑 + 播放引擎，不引用 WPF
    Models/                     Track / LyricLine / PlayMode / NowPlayingLayout
    Library/                    LibraryScanner、NaturalStringComparer、
                                MetadataLoader（TagLib#）、TrackFilter（搜索排序）
    Lyrics/                     LrcParser、LyricsProvider
    Playback/                   PlaybackQueue（顺序逻辑）、ReplayGain、
                                AudioSource、GaplessSampleProvider、PlayerEngine
    Support/                    Preferences（JSON）、TimeFormat
    ViewModels/                 PlayerViewModel（UI 唯一数据源）
  WinMusicPlayer/               WPF 外壳
    Views/                      TrackListView / NowPlayingView / LyricsView /
                                ControlsBar / SettingsPanel / LayoutThumbnail
    Interop/                    WindowBackdrop（亚克力）、ClickThrough（鼠标穿透）
    DesktopLyricsWindow         桌面歌词浮层
    TrayIcon                    托盘常驻
tests/MusicCore.Tests/          LrcParser / PlaybackQueue / TrackFilter /
                                ReplayGain / LibraryScanner / LyricsProvider
```

`PlaybackQueue`、`LrcParser`、`TrackFilter`、`ReplayGain`、`NaturalStringComparer`
都是不碰音频设备的纯逻辑，可完整单测；`PlayerEngine`、`MetadataLoader` 依赖真实音频文件
与输出设备，由手动验收覆盖。

## 已知限制

- 不做在线歌词下载、标签编辑、均衡器、媒体键集成
- 桌面歌词不支持逐字卡拉 OK 效果（需要增强型 LRC，普通 `.lrc` 没有这个信息）
- WAV 没有标准歌词标签，只能靠同名 `.lrc`
- 未做代码签名，SmartScreen 首次运行会提示「未知发布者」，点「仍要运行」即可
