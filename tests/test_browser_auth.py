from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "engine"))
from browser_auth import cookie_arguments, download_error, auth_description


class BrowserAuthTests(unittest.TestCase):
    def test_edge_read_does_not_export_cookie_file(self):
        args = cookie_arguments({"cookie_source": "edge", "edge_profile": "Default", "cookies": "old-file.txt"})
        self.assertEqual(args, ["--cookies-from-browser", "edge:Default"])
        self.assertNotIn("--cookies", args)

    def test_profile_spaces_are_kept_in_one_argument(self):
        self.assertEqual(cookie_arguments({"cookie_source": "edge", "edge_profile": "Profile 2"})[1], "edge:Profile 2")

    def test_invalid_profile_rejected(self):
        for profile in ["../../Default", "Default::container", "chrome", "Default --exec calc"]:
            with self.subTest(profile=profile), self.assertRaises(ValueError):
                cookie_arguments({"cookie_source": "edge", "edge_profile": profile})

    def test_legacy_file_projects_and_explicit_no_login(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cookies.txt"
            path.write_text("# Netscape HTTP Cookie File\n")
            self.assertEqual(cookie_arguments({"cookies": str(path)}), ["--cookies", str(path)])
            self.assertEqual(cookie_arguments({"cookies": str(path), "cookie_source": "none"}), [])
        self.assertEqual(cookie_arguments({}), [])

    def test_lock_and_decryption_have_distinct_guidance(self):
        self.assertIn("关闭所有 Edge", download_error("Could not copy Chrome cookie database"))
        self.assertIn("未必能解决", download_error("failed to decrypt with DPAPI"))
        self.assertIn("仍要求登录", download_error("Sign in to confirm you're not a bot"))
        self.assertEqual(download_error("Network timeout"), "Network timeout")

    def test_rejection_identifies_auth_mode(self):
        error = "Sign in to confirm you're not a bot"
        self.assertIn("没有使用登录状态", download_error(error, {"cookie_source": "none"}))
        self.assertIn("所选 Cookie 文件", download_error(error, {"cookie_source": "file"}))
        self.assertIn("所选 Edge 资料", download_error(error, {"cookie_source": "edge"}))

    def test_auth_diagnostic_omits_cookie_path(self):
        self.assertNotIn("private-cookie.txt", auth_description({"cookie_source": "file", "cookies": "private-cookie.txt"}))
        self.assertIn("Profile 2", auth_description({"cookie_source": "edge", "edge_profile": "Profile 2"}))
        self.assertIn("匿名", auth_description({"cookie_source": "none"}))


if __name__ == "__main__": unittest.main()
