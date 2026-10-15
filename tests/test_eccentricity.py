"""Contract tests for automatic accidental eccentricity in CScore pile orchestrator.

These tests verify the force transformation and audit trail, not SP63 validation.
"""
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import types

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import pile_rebar as app


class EccentricityTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT / "examples/demo_bns.json").read_text(encoding="utf-8"))
        self.data["pile"]["diameter_mm"] = 800
        self.data["eccentricity"] = {
            "mode": "sp63_stat_determinate",
            "static_scheme": "statically_determinate",
            "effective_length_mm": 3000,
        }
        self.data["loads"] = [
            {"id":"cx","n_kn":-2000,"mx_knm":200,"my_knm":0},
            {"id":"cy","n_kn":-1000,"mx_knm":0,"my_knm":-100},
            {"id":"biax","n_kn":-1500,"mx_knm":300,"my_knm":400},
            {"id":"tension","n_kn":500,"mx_knm":200,"my_knm":0},
            {"id":"bending","n_kn":0,"mx_knm":200,"my_knm":0},
        ]

    def test_correct_length_and_bar_size_ea(self):
        self.assertAlmostEqual(app.prepare_design_loads(self.data)[2]["ea_mm"],800/30)
        self.data["eccentricity"]["effective_length_mm"] = 30000
        self.assertAlmostEqual(app.prepare_design_loads(self.data)[2]["ea_mm"],50)

    def test_compression_sign_and_direction(self):
        effective, audit, opt = app.prepare_design_loads(self.data)
        ea=800/30/1000
        self.assertEqual(len(effective),5)
        self.assertAlmostEqual(effective[0]["mx_knm"],200+2000*ea)
        self.assertAlmostEqual(effective[1]["my_knm"],-100-1000*ea)
        self.assertAlmostEqual(effective[2]["mx_knm"],300+1500*ea*0.6)
        self.assertAlmostEqual(effective[2]["my_knm"],400+1500*ea*0.8)
        self.assertEqual(effective[3], self.data["loads"][3])
        self.assertEqual(effective[4], self.data["loads"][4])
        self.assertEqual(self.data["loads"][0]["mx_knm"],200)
        self.assertEqual(audit[0]["original"]["mx_knm"],200)
        self.assertAlmostEqual(audit[0]["effective"]["mx_knm"],effective[0]["mx_knm"])
        self.assertTrue(opt["eccentricity_accounted_for"])

    def test_no_double_count_when_already_in_loads(self):
        self.data["eccentricity"]={"mode":"already_in_loads"}
        effective,audit,opt=app.prepare_design_loads(self.data)
        self.assertEqual(effective,self.data["loads"])
        self.assertEqual(audit[0]["ea_mm"],0)
        self.assertTrue(opt["eccentricity_accounted_for"])

    def test_research_only_mode_blocks_preselection(self):
        self.data["eccentricity"]={"mode":"research_none"}
        self.assertFalse(app.prepare_design_loads(self.data)[2]["eccentricity_accounted_for"])
        self.data.pop("eccentricity")
        self.assertFalse(app.prepare_design_loads(self.data)[2]["eccentricity_accounted_for"])

    def test_centric_compression_requires_explicit_direction(self):
        self.data["loads"]=[{"id":"centric","n_kn":-1000,"mx_knm":0,"my_knm":0}]
        with self.assertRaisesRegex(app.InputError,"zero_moment_axis"):
            app.prepare_design_loads(self.data)
        self.data["eccentricity"]["zero_moment_axis"]="my_negative"
        force=app.prepare_design_loads(self.data)[0][0]
        self.assertEqual(force["mx_knm"],0)
        self.assertAlmostEqual(force["my_knm"],-1000*(800/30)/1000)

    def test_invalid_configuration_fails_closed(self):
        self.data["eccentricity"]["static_scheme"]="indeterminate"
        with self.assertRaises(app.InputError):
            app.validate_input(self.data)
        self.data["eccentricity"]["static_scheme"]="statically_determinate"
        self.data["eccentricity"]["effective_length_mm"]=-3000
        with self.assertRaises(app.InputError):
            app.validate_input(self.data)
        self.data["eccentricity"]["effective_length_mm"]=3000
        self.data["eccentricity"]["zero_moment_axis"]="unknown"
        with self.assertRaises(app.InputError):
            app.validate_input(self.data)

    def test_runner_receives_transformed_loads_and_csv_records_original(self):
        # End-to-end orchestration uses fake numerical results, NEVER claims CScore validity.
        self.data["search"]={"bar_counts":[8],"bar_diameters_mm":[20],"rotation_deg":[0]}
        self.data["loads"]=self.data["loads"][:2]
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder)
            inp=base/"input.json"
            inp.write_text(json.dumps(self.data),encoding="utf-8")
            result=base/"result.json"
            table=base/"result.csv"
            def runner_fake(argv, **kwargs):
                req=json.loads(kwargs["input"])
                expected=app.prepare_design_loads(self.data)[0]
                self.assertEqual(req["loads"], expected)
                return types.SimpleNamespace(
                    returncode=0,stderr="",
                    stdout=json.dumps({"schema_version":1,"cases":[
                        {"id":x["id"],"loads":[{"id":l["id"],"converged":True,
                         "factor":1.5,"utilization":2/3,"error":None} for l in req["loads"]]}
                        for x in req["candidates"]]}))
            with patch.object(app.subprocess,"run",side_effect=runner_fake):
                r=app.run(inp,base/"fake.exe",result,table)
            self.assertTrue(r["eccentricity"]["eccentricity_accounted_for"])
            self.assertEqual(r["selected_uls_candidate"],"8x20@0deg")
            self.assertEqual(r["forces_audit"][0]["original"]["mx_knm"],200)
            self.assertGreater(r["forces_audit"][0]["effective"]["mx_knm"],200)
            self.assertIn("original_mx_knm",table.read_text(encoding="utf-8-sig"))
            self.assertIn("effective_mx_knm",table.read_text(encoding="utf-8-sig"))


if __name__ == "__main__":
    unittest.main()
