#!/usr/bin/env python3
"""Run the first Multiplayer live-test batch through RIMAPI; write JSON and Markdown evidence."""

import argparse
import importlib.util
import json
from pathlib import Path
import re
import time
import zipfile
from urllib.request import ProxyHandler, build_opener

ANSI = re.compile(r"\x1b\[[0-9;]*m")
FIXTURE_KEYS = ("map_id", "thing_id", "zone_id", "sequence", "stack_count", "allows_steel", "pawns", "generated_pawn", "second_map_id")


def fixture(snapshot):
    return {key: snapshot[key] for key in FIXTURE_KEYS}


def logged_errors(text):
    # Multiplayer's network logger can put semantic errors inside an INFO line.
    return [line for line in ANSI.sub("", text).splitlines()
            if re.match(r"^\d\d:\d\d:\d\d\s+(ERROR|FATAL)\b", line) or "INFO MpServerLog Error:" in line]


def assert_pair(snapshots):
    for value in snapshots:
        if not value["connected"] or value["desynced"] or value["simulating"] or len(value["players"] or []) != 2:
            raise AssertionError("Peers must both be playing, caught up and not desynced: " + str(snapshots))
    if snapshots[0]["players"] != snapshots[1]["players"]:
        raise AssertionError("Player lists differ")
    if fixture(snapshots[0]) != fixture(snapshots[1]):
        raise AssertionError("Peer fixtures differ: " + str(snapshots))


class Peer:
    def __init__(self, role, base, log, runner, evidence):
        self.role, self.base, self.log = role, base.rstrip("/"), Path(log)
        self.runner, self.evidence = runner, evidence
        self.opener = build_opener(ProxyHandler({}))

    def ready(self, playing):
        deadline = time.monotonic() + 180
        while True:
            try:
                with self.opener.open(self.base + "/api/v1/game/state", timeout=4) as response:
                    value = json.load(response)
                if value.get("success") is True and (not playing or value["data"]["program_state"] == "Playing"):
                    return value
            except (OSError, ValueError, KeyError):
                pass
            if time.monotonic() >= deadline:
                raise TimeoutError(self.role + " API did not become ready")
            time.sleep(1)

    def action(self, name):
        args = self.runner.parser().parse_args([
            "Live: " + name, "--category", "Multiplayer live tests", "--assembly", "MultiplayerLiveTests",
            "--base-url", self.base, "--request-timeout", "20"
        ])
        result = self.runner.invoke(args)  # Fresh exact discovery; uncertain POSTs are never retried.
        self.evidence.append({"peer": self.role, "action": name, "invocation": result})
        return result

    def event(self, name, phase, timeout=15):
        stat = self.log.stat()
        offset = stat.st_size
        self.action(name)
        deadline = time.monotonic() + timeout
        while True:
            current = self.log.stat()
            if current.st_ino != stat.st_ino or current.st_size < offset:
                raise AssertionError("Log replaced or truncated")
            with self.log.open("rb") as stream:
                stream.seek(offset)
                data = stream.read()
            for raw in data.split(b"\n")[:-1]:
                line = ANSI.sub("", raw.decode("utf-8", errors="replace"))
                if "[MP-LIVE] {" in line:
                    value = json.loads(line.split("[MP-LIVE] ", 1)[1])
                    if value["phase"] == phase:
                        self.evidence.append({"peer": self.role, "event": value})
                        return value["data"]
            if time.monotonic() >= deadline:
                raise TimeoutError("No fresh " + phase + " event from " + self.role)
            time.sleep(.1)

    def snapshot(self):
        return self.event("snapshot", "snapshot")


def wait_pair(peers, condition=lambda pair: True, timeout=60):
    deadline = time.monotonic() + timeout
    last = None
    while True:
        pair = [peer.snapshot() for peer in peers]
        if any(value["desynced"] for value in pair):
            raise AssertionError("Desync detected: " + str(pair))
        try:
            assert_pair(pair)
            if condition(pair):
                return pair
        except AssertionError as error:
            last = str(error)
        if time.monotonic() >= deadline:
            raise TimeoutError("Peers did not converge: " + str(last or pair))
        time.sleep(.5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("session", type=Path, help="session.json created by session.py")
    parser.add_argument("--skills-root", required=True, type=Path)
    args = parser.parse_args()
    config = json.loads(args.session.read_text())
    run = args.session.parent
    cases = json.loads(Path(__file__).with_name("cases.json").read_text())
    for case in cases:
        case["status"] = "NOT RUN"
    evidence = []
    spec = importlib.util.spec_from_file_location("checked_runner", args.skills_root / "skills/use-rimapi-debug-actions/scripts/run_debug_action.py")
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    peers = [Peer(role, config[role]["base_url"], config[role]["log"], runner, evidence) for role in ("host", "client")]
    host, client = peers
    started = time.time()
    current = None
    error = None

    def step(index, work):
        nonlocal current
        current = cases[index]
        print(current["id"] + " " + current["name"], flush=True)
        result = work()
        current["status"] = "PASS"
        current["evidence"] = result

    try:
        def connect():
            host.ready(True)
            client.ready(False)
            host.action("host")
            # Hosting is asynchronous. Wait for its actual playing state before joining.
            deadline = time.monotonic() + 60
            while not host.snapshot()["connected"]:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Host did not start")
                time.sleep(.5)
            client.action("join")
            return wait_pair(peers)
        step(0, connect)

        def setup():
            host.action("setup stockpile")
            return wait_pair(peers, lambda p: p[0]["sequence"] == 1 and p[0]["stack_count"] == 17)
        step(1, setup)

        def increment(peer, sequence, count):
            peer.action("add stack")
            return wait_pair(peers, lambda p: p[0]["sequence"] == sequence and p[0]["stack_count"] == count)
        step(2, lambda: increment(host, 2, 18))
        step(3, lambda: increment(client, 3, 19))

        def cache():
            return [peer.event("stockpile cache", "cache_checked") for peer in peers]
        step(4, cache)

        def filters():
            host.action("disallow steel")
            first = wait_pair(peers, lambda p: p[0]["allows_steel"] is False)
            cache()
            client.action("allow steel")
            second = wait_pair(peers, lambda p: p[0]["allows_steel"] is True)
            cache()
            return {"disallowed": first, "allowed": second}
        step(5, filters)

        def advance():
            before = wait_pair(peers)
            host.action("resume")
            advanced = wait_pair(peers, lambda p: all(v["game_tick"] >= before[i]["game_tick"] + 180 for i, v in enumerate(p)))
            host.action("pause")
            paused = wait_pair(peers, lambda p: p[0]["game_tick"] == p[1]["game_tick"])
            time.sleep(1)
            stable = wait_pair(peers)
            if [v["game_tick"] for v in paused] != [v["game_tick"] for v in stable]:
                raise AssertionError("Pause did not stop game ticks")
            # This needs an actual successful synchronization comparison, not just absence of a desync flag.
            if stable[1]["last_valid_tick"] <= before[1]["last_valid_tick"]:
                raise AssertionError("Client sync opinions did not advance")
            return {"before": before, "advanced": advanced, "paused": stable}
        step(6, advance)

        def pawn():
            before = wait_pair(peers)
            host.action("generate pawn")
            return wait_pair(peers, lambda p: p[0]["generated_pawn"] is not None and len(p[0]["pawns"]) == len(before[0]["pawns"]) + 1)
        step(7, pawn)

        # Hook installation is testable without DLC. Actual role-assignment fixtures are not.
        features = wait_pair(peers)[0]
        for index, feature, expansion in ((8, "ideology", "Ideology"), (9, "anomaly", "Anomaly")):
            if not features[feature]:
                cases[index].update(status="BLOCKED", reason=expansion + " is not installed/enabled; full ritual fixture requires it")
        step(15, lambda: [p.event("ritual hooks", "ritual_hooks_checked") for p in peers])

        def reload():
            before = wait_pair(peers)
            for p in peers:
                p.event("arm reload", "reload_armed")
            host.action("save reload")
            after = wait_pair(peers, lambda p: all(v["reload_reconstructed"] and v["snapshot_tick"] > before[i]["snapshot_tick"] for i,v in enumerate(p)), timeout=90)
            events = [p.event("save archive", "archive_saved") for p in peers]
            for event in events:
                path = Path(event["path"])
                if not path.is_file() or path.stat().st_size != event["bytes"]:
                    raise AssertionError("Multiplayer archive was not written")
                with zipfile.ZipFile(path) as archive:
                    if archive.testzip() is not None or not archive.namelist():
                        raise AssertionError("Invalid Multiplayer save archive")
            if fixture(before[0]) != fixture(after[0]):
                raise AssertionError("Save/reload changed the fixture")
            return {"before": before, "after": after, "reloads": events}
        step(10, reload)

        def rejoin():
            before = wait_pair(peers)
            client.action("rejoin")
            after = wait_pair(peers, lambda p: p[1]["rejoin_reconstructed"] and p[1]["last_valid_tick"] >= before[1]["last_valid_tick"], timeout=120)
            if fixture(before[0]) != fixture(after[0]):
                raise AssertionError("Rejoin changed the fixture")
            return {"before": before, "after": after}
        step(11, rejoin)

        def maps():
            host.action("second map")
            both = wait_pair(peers, lambda p: p[0]["second_map_id"] >= 0 and all(len(v["maps"]) == 2 for v in p), timeout=120)
            first_id, second_id = both[0]["map_id"], both[0]["second_map_id"]
            def by_id(value, map_id):
                return next(m for m in value["maps"] if m["id"] == map_id)
            client.action("view second map")
            split = wait_pair(peers, lambda p: all(by_id(v, first_id)["viewers"] == 1 and by_id(v, second_id)["viewers"] == 1 for v in p))
            host.action("resume first map")
            advancing = wait_pair(peers, lambda p: all(by_id(v, first_id)["ticks"] > by_id(split[i], first_id)["ticks"] + 120 for i,v in enumerate(p)))
            host.action("pause")
            paused = wait_pair(peers, lambda p: all(m["speed"] == "Paused" for v in p for m in v["maps"]))
            if any(by_id(v, second_id)["ticks"] != by_id(split[i], second_id)["ticks"] for i,v in enumerate(paused)):
                raise AssertionError("Paused second map advanced with first map")
            client.action("view first map")
            together = wait_pair(peers, lambda p: all(by_id(v, first_id)["viewers"] == 2 and by_id(v, second_id)["viewers"] == 0 for v in p))
            return {"split": split, "advancing": advancing, "paused": paused, "together": together}
        step(12, maps)
        step(13, lambda: [p.event("marker cleanup", "markers_checked") for p in peers])

        def reconnect():
            before = wait_pair(peers)
            client.event("disconnect", "disconnected")
            deadline = time.monotonic() + 30
            while len(host.snapshot()["players"]) != 1:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Server did not observe client disconnect")
                time.sleep(.5)
            while not client.snapshot()["entry_scene"]:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Client did not return to the entry scene")
                time.sleep(.5)
            client.action("join")
            def caught_up(p):
                return (p[1]["player_id"] != before[1]["player_id"] and len(p[1]["maps"]) == 2
                        and all(m["viewers"] == (2 if m["id"] == v["map_id"] else 0) for v in p for m in v["maps"]))
            after = wait_pair(peers, caught_up, timeout=120)
            if fixture(before[0]) != fixture(after[0]):
                raise AssertionError("Fresh connection lost fixture state")
            return {"before": before, "after": after}
        step(16, reconnect)

        def desync():
            before = wait_pair(peers)
            client.event("inject desync", "desync_injected")
            host.action("resume")
            deadline = time.monotonic() + 45
            while True:
                value = client.snapshot()
                if value["desynced"]:
                    return {"before": before, "detected": value}
                if time.monotonic() >= deadline:
                    raise AssertionError("Intentional RNG mismatch was not detected")
                time.sleep(.5)
        # Intentional divergence must be the final case; subsequent pair checks would be invalid.
        step(14, desync)
    except Exception as failure:
        error = str(failure)
        if current:
            current["status"], current["error"] = "FAIL", error
        print("FAILED: " + error, flush=True)
    finally:
        # A run failure must not leave the colony running indefinitely.
        try:
            if host.snapshot()["connected"]:
                host.action("pause")
        except Exception:
            pass
        game_errors = {}
        for peer in peers:
            game_errors[peer.role] = logged_errors(peer.log.read_text(errors="replace"))
        if not error and any(game_errors.values()):
            error = "Case assertions passed, but game/network log errors were observed; inspect game_errors"
        report = {"status": "FAIL" if error or any(game_errors.values()) else "PASS", "duration_seconds": round(time.time()-started, 2),
                  "error": error, "cases": cases, "game_errors": game_errors, "evidence": evidence, "session": config}
        (run/"report.json").write_text(json.dumps(report, indent=2))
        markdown = "# Multiplayer live test report\n\nImplemented smoke batch: **" + report["status"] + "**. Coverage beyond this batch remains listed below.\n\n| Case | Result | Description |\n|---|---|---|\n"
        markdown += "".join(f'| {c["id"]} | {c["status"]} | {c["name"]} |\n' for c in cases)
        markdown += "\n" + (error or "Fresh API action evidence and peer snapshots are in report.json.")
        markdown += "\n\nGame errors: " + json.dumps(game_errors) + "\n"
        (run/"report.md").write_text(markdown)
        print("Report: " + str(run/"report.md"), flush=True)
    return 1 if report["status"] == "FAIL" else 0


if __name__ == "__main__":
    raise SystemExit(main())
