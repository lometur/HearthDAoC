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
        s = gen_config.settings({"OFFLINEDAOC_PORT": "10311", "OFFLINEDAOC_UDP_PORT": "10411",
                                 "OFFLINEDAOC_AUTO_ACCOUNTS": "false", "OFFLINEDAOC_LISTEN_IP": "192.168.1.64"})
        cfg = parse(gen_config.render(s, "/data"))
        self.assertEqual((cfg["Port"], cfg["UdpPort"], cfg["AutoAccountCreation"]), ("10311", "10411", "False"))
        self.assertEqual((cfg["IP"], cfg["RegionIP"], cfg["UdpIP"]), ("192.168.1.64",) * 3)

    def test_server_name_is_xml_escaped(self):
        cfg = parse(gen_config.render(gen_config.settings({"OFFLINEDAOC_SERVER_NAME": "Bob & <Friends>"}), "/data"))
        self.assertEqual(cfg["ServerName"], "Bob & <Friends>")

    def test_invalid_values(self):
        for env, msg in [({"OFFLINEDAOC_PORT": "70000"}, "OFFLINEDAOC_PORT"),
                         ({"OFFLINEDAOC_UDP_PORT": "abc"}, "OFFLINEDAOC_UDP_PORT"),
                         ({"OFFLINEDAOC_LISTEN_IP": "my-host"}, "OFFLINEDAOC_LISTEN_IP"),
                         ({"OFFLINEDAOC_AUTO_ACCOUNTS": "maybe"}, "OFFLINEDAOC_AUTO_ACCOUNTS"),
                         ({"OFFLINEDAOC_SERVER_NAME": "   "}, "OFFLINEDAOC_SERVER_NAME")]:
            with self.subTest(env=env), self.assertRaisesRegex(gen_config.ConfigError, msg):
                gen_config.settings(env)

    def test_cli_writes_file_and_reports_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "serverconfig.xml")
            env = dict(os.environ, OFFLINEDAOC_PORT="10391")
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            with open(out, encoding="utf-8") as f:
                self.assertEqual(parse(f.read())["Port"], "10391")
            env["OFFLINEDAOC_PORT"] = "0"
            r = subprocess.run([sys.executable, os.path.join(BIN, "gen_config.py"), "--data", "/data", "--out", out],
                               env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("ERROR: OFFLINEDAOC_PORT", r.stderr)


if __name__ == "__main__":
    unittest.main()
