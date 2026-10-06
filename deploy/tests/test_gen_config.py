import os
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import gen_config  # noqa: E402


def parse(xml):
    return {child.tag: (child.text or "") for child in ET.fromstring(xml).find("Server")}


class GenConfigTests(unittest.TestCase):
    def test_defaults(self):
        cfg = parse(gen_config.render(gen_config.settings({}), "/data"))
        self.assertEqual(cfg["Port"], "10301")
        self.assertEqual(cfg["UdpPort"], "10401")
        self.assertEqual(cfg["RegionPort"], "10401")
        self.assertEqual(cfg["IP"], "0.0.0.0")
        self.assertEqual(cfg["EnableUPnP"], "False")
        self.assertEqual(cfg["DetectRegionIP"], "False")
        self.assertEqual(cfg["AutoAccountCreation"], "True")
        self.assertEqual(cfg["DBType"], "SQLITE")
        self.assertIn("Data Source=/data/world/opendaoc.sqlite3.db;", cfg["DBConnectionString"])
        self.assertEqual(cfg["MetricsEnabled"], "false")

    def test_overrides_and_case_insensitive_bool(self):
        s = gen_config.settings({"HEARTHDAOC_PORT": "10311", "HEARTHDAOC_UDP_PORT": "10411",
                                 "HEARTHDAOC_AUTO_ACCOUNTS": "false", "HEARTHDAOC_LISTEN_IP": "192.168.1.64"})
        cfg = parse(gen_config.render(s, "/data"))
        self.assertEqual((cfg["Port"], cfg["UdpPort"], cfg["AutoAccountCreation"]), ("10311", "10411", "False"))
        self.assertEqual((cfg["IP"], cfg["RegionIP"], cfg["UdpIP"]), ("192.168.1.64",) * 3)

    def test_autosave_defaults_to_five_minutes(self):
        cfg = parse(gen_config.render(gen_config.settings({}), "/data"))
        self.assertEqual((cfg["DBAutosave"], cfg["DBAutosaveInterval"]), ("True", "5"))

    def test_autosave_minutes_can_be_set(self):
        s = gen_config.settings({"HEARTHDAOC_AUTOSAVE_MINUTES": "15"})
        self.assertEqual(parse(gen_config.render(s, "/data"))["DBAutosaveInterval"], "15")

    def test_invalid_autosave_and_backup_keep_are_refused(self):
        for key, bad in [("HEARTHDAOC_AUTOSAVE_MINUTES", "0"), ("HEARTHDAOC_AUTOSAVE_MINUTES", "61"),
                         ("HEARTHDAOC_AUTOSAVE_MINUTES", "5m"), ("HEARTHDAOC_BACKUP_KEEP", "seven"),
                         ("HEARTHDAOC_BACKUP_KEEP", "0"), ("HEARTHDAOC_BACKUP_KEEP", "-3")]:
            with self.subTest(key=key, value=bad), self.assertRaisesRegex(gen_config.ConfigError, key):
                gen_config.settings({key: bad})

    def test_backup_keep_accepts_a_whole_number(self):
        self.assertEqual(gen_config.settings({"HEARTHDAOC_BACKUP_KEEP": "14"})["HEARTHDAOC_BACKUP_KEEP"], "14")

    def test_server_name_is_xml_escaped(self):
        cfg = parse(gen_config.render(gen_config.settings({"HEARTHDAOC_SERVER_NAME": "Bob & <Friends>"}), "/data"))
        self.assertEqual(cfg["ServerName"], "Bob & <Friends>")

    def test_invalid_values(self):
        for env, msg in [({"HEARTHDAOC_PORT": "70000"}, "HEARTHDAOC_PORT"),
                         ({"HEARTHDAOC_UDP_PORT": "abc"}, "HEARTHDAOC_UDP_PORT"),
                         ({"HEARTHDAOC_LISTEN_IP": "my-host"}, "HEARTHDAOC_LISTEN_IP"),
                         ({"HEARTHDAOC_AUTO_ACCOUNTS": "maybe"}, "HEARTHDAOC_AUTO_ACCOUNTS"),
                         ({"HEARTHDAOC_SERVER_NAME": "   "}, "HEARTHDAOC_SERVER_NAME")]:
            with self.subTest(env=env), self.assertRaisesRegex(gen_config.ConfigError, msg):
                gen_config.settings(env)

    def test_cli_writes_file_and_reports_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "serverconfig.xml")
            env = dict(os.environ, HEARTHDAOC_PORT="10391")
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            with open(out, encoding="utf-8") as f:
                self.assertEqual(parse(f.read())["Port"], "10391")
            env["HEARTHDAOC_PORT"] = "0"
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("ERROR: HEARTHDAOC_PORT", r.stderr)


if __name__ == "__main__":
    unittest.main()
