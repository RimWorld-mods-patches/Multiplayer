#!/usr/bin/env python3
"""Launch or stop two muted macOS RimWorld test processes using disposable profiles and existing API builds."""

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time
import xml.etree.ElementTree as ET


def mod_id(path):
    return ET.parse(path / "About/About.xml").getroot().findtext("packageId").lower()


def ensure_link(mods, source, name):
    # Reuse existing links to avoid loading the same package ID twice.
    for installed in mods.iterdir():
        if installed.resolve() == source.resolve():
            return installed.name
    link = mods / name
    if link.exists() or link.is_symlink():
        raise ValueError("Refusing to replace existing mod: " + str(link))
    link.symlink_to(source.resolve(), target_is_directory=True)
    return name


def xml_file(path, root_name, values):
    root = ET.Element(root_name)
    for name, value in values.items():
        ET.SubElement(root, name).text = str(value)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def stop(path):
    config = json.loads(path.read_text())
    for role in ("client", "host"):
        if role not in config:
            continue
        peer = config[role]
        result = subprocess.run(["ps", "-p", str(peer["pid"]), "-o", "command="], capture_output=True, text=True)
        if result.returncode:
            continue
        if "-savedatafolder=" + peer["profile"] not in result.stdout or config["executable"] not in result.stdout:
            raise ValueError("PID no longer belongs to the test profile; refusing to signal it")
        os.kill(peer["pid"], signal.SIGTERM)
        for _ in range(50):
            if subprocess.run(["ps", "-p", str(peer["pid"])], stdout=subprocess.DEVNULL).returncode:
                break
            time.sleep(.1)
        else:
            raise TimeoutError("Test game did not stop: " + str(peer["pid"]))


def launch(args):
    repo = Path(__file__).resolve().parents[2]
    args.game = args.game.resolve()
    args.api_mod = args.api_mod.resolve()
    args.companion_mod = args.companion_mod.resolve()
    executable = args.game / "Contents/MacOS/RimWorld"
    mods = args.game.parent / "Mods"
    test_mod = repo / "artifacts/live-tests/mod"
    for source in (args.api_mod, args.companion_mod, test_mod):
        if not (source / "About/About.xml").is_file():
            raise ValueError("Missing mod metadata: " + str(source))
        if not list((source / "1.7/Assemblies").glob("*.dll")):
            raise ValueError("Missing 1.7 test assemblies: " + str(source))
    if not executable.is_file() or not mods.is_dir():
        raise ValueError("Expected the 1.7 macOS executable and its Mods directory")
    for port in (8767, 8768):
        try:
            connection = socket.create_connection(("localhost", port), timeout=.5)
        except OSError:
            continue
        connection.close()
        raise ValueError(f"Port {port} is already in use; stop the previous test session first")
    api_folder = ensure_link(mods, args.api_mod, "dev-mp-api-live")
    ensure_link(mods, args.companion_mod, "dev-mp-api-actions-live")
    ensure_link(mods, test_mod, "dev-mp-live-tests")
    run = repo / "artifacts/test-runs" / ("live-suite-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
    run.mkdir(parents=True)
    config = {"executable": str(executable), "rimworld_version": "1.7", "muted": True,
              "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
              "test_dll_sha256": hashlib.sha256((test_mod/"1.7/Assemblies/MultiplayerLiveTests.dll").read_bytes()).hexdigest()}
    for role, port, username, x in (("host", 8767, "MPLiveHost", 30), ("client", 8768, "MPLiveClient", 1080)):
        profile = run / ".runtime" / role
        settings_dir = profile / "Config"
        settings_dir.mkdir(parents=True)
        (profile/"Saves").mkdir()
        xml_file(settings_dir/"Prefs.xml", "PrefsData", {
            "volumeMaster": 0, "volumeMusic": 0, "volumeGame": 0, "volumeAmbient": 0, "volumeUI": 0,
            "screenWidth": 1024, "screenHeight": 768, "windowPositionX": x, "windowPositionY": 50,
            "fullscreen": "False", "uiScale": 1, "runInBackground": "True", "devMode": "True",
            "pauseOnError": "False", "openLogOnWarnings": "False", "resetModsConfigOnCrash": "False"
        })
        root = ET.Element("ModsConfigData")
        ET.SubElement(root, "version").text = "1.7.4990 rev1069"
        active = ET.SubElement(root, "activeMods")
        for package in ("brrainz.harmony_steam", "ludeon.rimworld", "rwmt.multiplayer", mod_id(test_mod), mod_id(args.api_mod), mod_id(args.companion_mod)):
            ET.SubElement(active, "li").text = package
        ET.SubElement(root, "knownExpansions")
        ET.ElementTree(root).write(settings_dir/"ModsConfig.xml", encoding="utf-8", xml_declaration=True)
        root = ET.Element("SettingsBlock")
        settings = ET.SubElement(root, "ModSettings", Class="RIMAPI.RIMAPI_Settings")
        for key, value in {"serverIP": "localhost", "serverPort": port, "enableLogging": "true", "loggingLevel": 1,
                           "enableCaching": "false", "cacheLogStatistics": "false"}.items():
            ET.SubElement(settings, key).text = str(value)
        ET.ElementTree(root).write(settings_dir/f"Mod_{api_folder}_RIMAPI_Mod.xml", encoding="utf-8", xml_declaration=True)
        log = run / (role + ".log")
        command = [str(executable), "-savedatafolder=" + str(profile), "-logFile", str(log), "-username=" + username]
        if role == "host":
            command.append("-quicktest")
        (run/"steam_appid.txt").write_text("294100")
        with log.open("wb") as output:
            process = subprocess.Popen(command, cwd=run, stdout=output, stderr=subprocess.STDOUT,
                                       env=dict(os.environ, SteamAppId="294100"), start_new_session=True)
        config[role] = {"pid": process.pid, "profile": str(profile), "log": str(log), "base_url": f"http://localhost:{port}", "command": command}
        # Persist after each launch so even a partial setup has exact cleanup information.
        (run/"session.json").write_text(json.dumps(config, indent=2))
    print(run/"session.json", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    start = commands.add_parser("launch")
    start.add_argument("--game", type=Path, required=True, help="RimWorld.app directory")
    start.add_argument("--api-mod", type=Path, required=True, help="existing experimental RIMAPI 1.7 mod")
    start.add_argument("--companion-mod", type=Path, required=True)
    end = commands.add_parser("stop")
    end.add_argument("session", type=Path)
    args = parser.parse_args()
    if args.command == "stop":
        stop(args.session)
    else:
        launch(args)


if __name__ == "__main__":
    main()
