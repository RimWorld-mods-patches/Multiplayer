"""Check that live-test acceptance rejects false passes from disconnected or divergent peers."""
import copy
import unittest

from run_suite import assert_pair, logged_errors


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        peer = dict(connected=True, desynced=False, simulating=False, players=["client", "host"],
                    map_id=0, thing_id=42, zone_id=1, sequence=3, stack_count=19,
                    allows_steel=True, pawns=[dict(id=2, name="Test Pawn")], generated_pawn=None, second_map_id=-1)
        self.pair = [peer, copy.deepcopy(peer)]

    def test_matching_peers_accepted(self):
        assert_pair(self.pair)

    def test_local_success_is_insufficient(self):
        for field, value in (("connected", False), ("desynced", True), ("simulating", True), ("players", ["host"])):
            with self.subTest(field=field):
                pair = copy.deepcopy(self.pair)
                pair[1][field] = value
                with self.assertRaises(AssertionError):
                    assert_pair(pair)

    def test_equal_counts_cannot_hide_different_objects(self):
        for field, value in (("thing_id", 99), ("zone_id", 99), ("sequence", 0), ("allows_steel", False),
                             ("pawns", [dict(id=2, name="Different Pawn")])):
            with self.subTest(field=field):
                pair = copy.deepcopy(self.pair)
                pair[1][field] = value
                with self.assertRaises(AssertionError):
                    assert_pair(pair)

    def test_network_errors_inside_info_are_not_hidden(self):
        lines = ["01:00:00 ERROR unexpected exception",
                 "01:00:01  INFO MpServerLog Error: No handler for packet Server_Cursor in state ClientLoading",
                 "01:00:02  INFO MPLiveClient Desynced after last valid tick 100: Wrong random state for the world"]
        self.assertEqual(logged_errors("\n".join(lines)), lines[:2])


if __name__ == "__main__":
    unittest.main()
