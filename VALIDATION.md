# VideoCN 验证记录

日期：2026-09-15。机器：Windows，RTX 4070 Ti 12GB，.NET SDK 10.0.401。

## 2026-09-15 人声段与长空挡保护

- 38 项 Python 测试通过；Release 自包含发布成功。WPF 实际编辑 29 行副本，验证保存/重开后人声段编号、有声秒数、逐词时间、手动配音位置均保留；新增重建按钮、有效时长预算和换行的配音提示。
- 在当前 396.366667 秒原视频上运行真实 VAD、Whisper large-v3 与 Qwen3 14B，检测到 12 个人声段、13 个非语音区间（合计约 241.52 秒），重新生成 29 条识别/翻译行。所有行均在人声段内，无行跨越长空挡；分别导出的有声与非语音 WAV 覆盖原音轨全部 17,479,168 个采样帧。
- 真实 Qwen3-TTS 生成 26 个中文配音句。第一轮有 6 句越过所在人声段边界；对 8 个候选句进行改写未得到更贴近目标的新文案，按提前结束规则保留第 1 轮完整配音。6 个段最终统一额外加速 1.059–1.234 倍，完成 FFmpeg 合成。该运行验证了无有效改写时的提前结束与段级加速；三轮上限及恢复较佳轮次由回归测试覆盖。
- 实际导出 MP4 仍为 396.366667 秒。逐段验证首句起点对齐、段内声音不重叠、不越过人声段边界；13 个空挡的中文音轨采样全部为零。开启背景声保留时，编码前的空挡音频与原音轨逐样本一致。所有最终边界误差均在 ±4 秒内。
- 合成音频另行覆盖统一段速、末尾声音保留、异采样率/声道背景转换、独立分段文件完整覆盖时间线、保留原片时长；这类测试不代表 AI 发音自然度。
- 已将验证成品和重新分段的项目放回 `output/20260914_231058_08fe01`；原项目、字幕和旧成片保存在该目录 `before-speech-*` 备份。未人工完整听审整条视频，专有名词、翻译准确性及段级加速后的听感仍需用户校对。
- 证据：`artifacts/speech-gap-repair/{segmentation-validation,export-validation}.json`、`render.log`；`artifacts/speech-gap-audio-verified/validation.json`；`artifacts/speech-gap-delivery-timing-ui/`。历史条目中的片尾延长/跨句顺延行为已被新人声段流程替代，旧元数据的兼容函数测试仍保留。

## 已通过

- 中文句末对齐更新：33 项 Python 测试通过，覆盖跨来源行合句、行内句号拆分、引号和小数保留、允许前移以避免累积延迟、不可达容差时保留完整声音、手动位置优先，以及三轮上限和恢复较佳轮次。按用户后续选择，设置默认启用、±4 秒，可收紧至 1–3 秒。
- 真实 Qwen3 翻译与 Qwen3-TTS 循环验证：3 条构造来源行按中文句末合并成 2 句。一个 ±2 秒可行样例在第 2 轮消除全部超差；更紧凑的来源时间样例从最大 3.74 秒偏差改善至 3.35 秒，保留较佳轮次、标注超差并完成导出。相同初稿放宽为 ±4 秒时，第 1 轮即可通过，无需额外改写。这些误差相对构造来源时间锚点，不是对真实英文音素的对齐测量。
- 每秒字数更新：26 项 Python 测试通过；真实 Qwen3 14B 文案调整将示例长句从 28 字减至 14 字（建议 9–15），短句从 4 字展开至 17 字（建议 12–20），未增加示例原文之外的事实。原文案已备份。此小样不证明所有视频的翻译准确性。
- 真实 FFmpeg 字速校准：两段同为 16 字的合成音频原长 3 秒 / 5 秒、字幕窗长 2 秒 / 8 秒，设定 4 字/秒后实测平均为 4.002 / 4.001 字/秒。此为音频时长测试，不是人工语音自然度评价。
- WPF 已用真实 242 句项目的副本验证字幕编辑保存、独立配音位置保存、每秒字数控制联动与建议范围显示。用户原项目未被测试修改。
- 超长配音更新：21 项 Python 测试通过。真实 FFmpeg 测试将 6.8 秒合成长音频放入 4 秒时间窗，限制速度为 1.5 倍但保留超出部分，后句顺延，5 秒测试视频成功延长导出；检查末尾音频有信号，并验证手动重叠位置按指定秒数混合、缓存齐全时不加载 TTS 模型。此为媒体行为测试，不是 AI 质量测试。
- 富文本兼容更新：21 项 C# 断言通过，使用虚构 Cookie 覆盖行尾 `&#x9;`、TSV 中 Markdown 转义、单独换行的优先级、附加链接列及 Cookie 值保真；不接受脱离完整数据行的优先级或其他任意文本。没有将用户实际凭据写入测试文件。
- Cookie 转换器更新：18 项 Python 测试、14 项 C# 转换断言通过。覆盖 Edge TSV / Markdown 表格、双语及调整顺序的表头、空值、Session、过期日期、HttpOnly / Secure、转义竖线与非法输入。
- WPF 实际打开粘贴窗口，通过「转换并使用」按钮保存合成测试 Cookie；验证两处登录选择同步为文件模式、设置持久化、错误输入不覆盖已有文件、值不进入运行日志。输出通过 Python MozillaCookieJar 的 Netscape 格式读取验证。
- 本次只使用明确标记的合成 Cookie 验证转换与接线，没有用合成凭据冒充真实 YouTube 登录成功。用户仍需粘贴自己的完整有效表格后测试访问。
- Release 编译：0 警告、0 错误。
- 自包含发布：`app/VideoCN.exe`，包含 .NET 运行时。
- 实际启动 WPF，渲染工作台 / 字幕校对 / 环境设置三个页面；修复了侧栏和主按钮的文字对比度。
- 桌面程序实际调用 Python 环境检查并解析 JSONL 结果。
- 取消实际 Python 父子进程：修复进程树取消边界问题后，子进程已退出，桌面界面恢复可用。
- 打开真实翻译项目的副本，在字幕编辑器修改中文、保存、重新打开；两行字幕恢复正确，修改持久化，生成中文视频按钮可用。
- 9 项核心测试：YouTube 链接规范化与非法主机拒绝、时间戳进位、无效/重叠字幕时间轴、ASS 字幕转义、调速链、中文路径和原子保存、断点继续及失败状态保存。
- 真实 FFmpeg 媒体测试：生成 6 秒视频，在中文及空格目录中提取音轨，烧录 ASS，混合两条音轨并导出 H.264 + AAC MP4。输出时长与原片相符，已查看生成的字幕画面。
- CUDA 运行库可用，GPU 名称正确；Whisper large-v3 已在真实英文测试视频中识别出两句话及时间戳。

## 完整真实 AI 流程

- 已用本机生成的 20 秒英文语音测试片跑通 Whisper large-v3 → Qwen3 14B → Demucs → Qwen3-TTS 1.7B → FFmpeg。
- 两句英文由 Whisper 实际识别，中文由 Qwen3 实际生成，没有预填翻译。片段时间为 0–4.96 秒、8.11–11.29 秒。
- TTS 使用 Serena 自然女声，生成的两段中文语音分别以 1.00×、1.01× 适配时间轴，没有触发超速保护，也没有人工修改这次测试的翻译。
- 原英文人声由 Demucs 分离，保留的背景轨与中文语音混合，最终生成 MP4、SRT、ASS。
- 成片与中文音轨均为 20.000 秒，中文音轨峰值约 0.335，无静音文件或明显削波；另用 Whisper 对生成语音重新识别，识别文本与两句中文相符。此项是机器可懂度检查，没有冒充人工试听评分。
- 高质量模型已缓存；快速档模型没有预先下载，首次选择快速档仍需要下载。
- 实际成片：`artifacts/ai-smoke/20260914_030427_bb3504/中文配音.mp4`。
- 本机其他应用占用显存时，Ollama 自动将部分翻译模型放在 CPU / RAM，测试翻译请求含启动约 51 秒；这不是长片性能基准。
- 当前使用 SDPA，未安装 FlashAttention；Qwen 包还会对未使用的旧版 25Hz 分支提示缺少 SoX。已验证的 12Hz CustomVoice 路径仍可完成配音。

## 外部限制

- Edge 后台处理更新：Release 自包含发布通过，17 项核心及认证测试通过；WPF 实际启动验证工作台与环境页登录选择双向同步、打开匿名旧项目后仍保留当前 Edge 登录设置、字幕编辑保存和子进程取消。工作台新增「结束 Edge 后台并重试」，由用户点击后结束当前会话的 Edge 后台进程并测试访问。
- 用户关闭 Edge 窗口后仍有 7 个后台进程。通过新版 WPF 中与按钮共用的方法，实际结束全部 7 个进程；检查时已无 `msedge.exe`，原先 Cookie 数据库占用错误消失。但随后 yt-dlp 返回 `Failed to decrypt with DPAPI`，软件显示简短的 Cookie 无法解密提示，完整堆栈保留在日志中。**直接读取 Edge 登录状态及带登录下载尚未成功**，需要其他可用认证来源。

- 早期匿名访问 YouTube 测试视频返回 `Sign in to confirm you're not a bot`，匿名下载未成功。程序支持自行提供 Cookie 文件，或明确选择 Edge 后尝试本地读取；验证期间没有导出 Cookie 文本文件。
- 此测试不证明所有 YouTube 视频可下载，也不证明长视频、多说话人、音乐背景复杂素材的成品质量。

## 证据

- `artifacts/sentence-alignment-{ai,feasible,four-seconds}-verified/validation.json`、`alignment-report.json`、`中文配音.mp4`：真实模型循环、达标/未达标分支及完整导出。
- `artifacts/sentence-alignment-final-ui/`：最终 ±4 秒默认值、控制联动及 242 行原项目副本的 WPF 验证。
- `artifacts/speech-rate-ai-verified/`：真实模型改写、备份和日志。
- `artifacts/speech-rate-audio-verified/validation.json`：平均字速实测。
- `artifacts/speech-rate-ui-verified/`：WPF 字速控制、242 句副本和页面渲染。
- `artifacts/dubbing-overflow-verified/validation.json` 与 `中文配音.mp4`：超长句、后句顺延、片尾延长及手动配音位置验证。
- `artifacts/cookie-converter-verified/cookie-import-smoke.json`、`cookie-import.png`、`page-*.png`：粘贴窗口、保存和登录模式切换的实际 WPF 验证；`synthetic-cookies/imported.txt` 仅含合成测试数据。
- `artifacts/edge-background-verified/edge-retry.json`：实际结束 7 个 Edge 后台进程、随后登录测试因解密失败而返回失败。
- `artifacts/edge-background-verified/{process-smoke,editor-smoke,ui-smoke}.json` 和 `page-*.png`：本次 WPF 运行、登录设置与页面渲染验证。
- `artifacts/run-20260914-130251-714.log`：数据库占用解除后的实际 DPAPI 解密错误。
- `artifacts/delivery-verified/process-smoke.json`：环境检查与取消子进程验证。
- `artifacts/delivery-verified/editor-smoke.json`：字幕编辑保存与重新打开验证。
- `artifacts/delivery-verified/page-*.png`：WPF 渲染截图。
- `artifacts/媒体 验证/validation.json`、`subtitle-frame.png`：FFmpeg 音视频及字幕验证。
- `artifacts/ai-smoke/`：本机生成的英文测试视频、识别项目和真实 AI 流程日志。
- `artifacts/youtube-probe.log`：YouTube 站点登录验证响应。
- `artifacts/installed-packages.txt`：实际安装的 Python 包版本快照。

未用合成测试音调或预写翻译冒充 AI 识别、翻译、配音效果。
