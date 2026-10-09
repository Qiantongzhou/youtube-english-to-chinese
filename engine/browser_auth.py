"""Explicit browser authentication selection. Never export browser cookies to disk."""
from pathlib import Path
import re


def cookie_arguments(settings):
    # Preserve projects created before the source selector existed.
    source = settings.get("cookie_source", "file" if settings.get("cookies") else "none")
    if source == "none":
        return []
    if source == "edge":
        profile = settings.get("edge_profile", "Default").strip() or "Default"
        if not re.fullmatch(r"Default|Profile [0-9]+", profile):
            raise ValueError("Edge 资料目录应为 Default 或 Profile 1 这样的名称。")
        return ["--cookies-from-browser", "edge:" + profile]
    if source == "file":
        cookies = settings.get("cookies", "")
        if not cookies or not Path(cookies).is_file():
            raise ValueError("请选择有效的 Cookie 文件，或切换到从 Edge 读取登录状态。")
        return ["--cookies", cookies]
    raise ValueError("未知的 YouTube 登录方式。")


def download_error(detail, settings=None):
    lower = detail.lower()
    if "could not copy" in lower and "cookie database" in lower:
        return "Edge 的 Cookie 数据库正在被占用。请先关闭所有 Edge 窗口，再点击工作台「结束 Edge 后台并重试」，由软件结束残留的 Edge 后台进程。关闭浏览器通常不会退出 YouTube 登录。"
    if any(s in lower for s in ("failed to decrypt", "dpapi", "app-bound", "app bound", "v20")):
        return "当前 Edge Cookie 无法解密，可能受到浏览器加密保护或 Windows 用户上下文限制。关闭浏览器未必能解决；可点击工作台「粘贴 Cookie 文本」转换 Edge 表格，或选择自行导出的 Cookie 文件。"
    if "could not find" in lower and "cookies database" in lower:
        return "没有找到所选 Edge 资料的 Cookie。请确认在该资料中登录过 YouTube，并选择正确的资料目录。"
    if "sign in to confirm" in lower:
        if settings is not None:
            source = settings.get("cookie_source", "file" if settings.get("cookies") else "none")
            if source == "none":
                return "本次下载没有使用登录状态，YouTube 拒绝了匿名访问。请在工作台将「YouTube 登录方式」改为「从 Edge 读取登录状态」，然后测试登录。若 Edge Cookie 被占用，需先关闭 Edge；也可选择 Cookie 文件。"
            if source == "file":
                return "已使用所选 Cookie 文件，但 YouTube 仍要求登录验证。文件可能已过期或不包含有效的 YouTube 登录信息，也可能是站点限制；请重新导出后测试。"
        return "YouTube 仍要求登录验证。请确认所选 Edge 资料已登录；即使成功读取 Cookie，站点也可能继续拒绝请求。可改用 Cookie 文件或导入本地视频。"
    return detail


def auth_description(settings):
    source = settings.get("cookie_source", "file" if settings.get("cookies") else "none")
    if source == "edge":
        return "本次请求：从 Edge 读取登录状态（" + (settings.get("edge_profile") or "Default") + "）。"
    if source == "file":
        return "本次请求：使用所选 Cookie 文件。"
    return "本次请求：不使用登录状态（匿名访问）。"
