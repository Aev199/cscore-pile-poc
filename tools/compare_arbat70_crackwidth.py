#!/usr/bin/env python3
"""Compare CScore research crack results against 70 independently run ARBAT cases.

No absent ARBAT width is construed as a zero. Does not approve any design.
Use --layout arbat_stated generated CScore research matrix; the geometric
interpretation of R400x600/R500x700/R400x400 remains a testable hypothesis.
"""
import argparse
import csv
import json
import math
from collections import defaultdict
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
REF=ROOT/"examples/verification/arbat_matched/arbat70_crackwidth_20261016.json"
case_names=["T01_pure_Mx","T02_high_Mx","T03_reverse_Mx","T04_My","T05_biax",
            "T06_compression","T07_tension","T08_low_Mx",
            "T09_partial_sustained","T10_partial_biax"]
GEOM_PENDING={"R400x600_6d20_cover50","R500x700_8d25_cover60",
              "R400x400_8d16_two_layers_cover35"}


def parse_num(value):
    if value in (None,""):
        return None
    number=float(value)
    if not math.isfinite(number):
        raise ValueError("CScore published nonfinite numerical value")
    return number


def compare(ref:dict,rows:list[dict])->tuple[list[dict],dict]:
    if len(rows)!=70 or len(ref["sections"])!=7:
        raise ValueError("Incomplete 70-case benchmark")
    lookup={(r["section_id"],r["case_id"]):r for r in rows}
    if len(lookup)!=70:
        raise ValueError("Duplicate section/load identification")
    if set(s for s,_ in lookup)!=set(ref["sections"]):
        raise ValueError("Different section IDs between ARBAT and CScore")
    records=[]
    for section,data in ref["sections"].items():
        if len(data["long"])!=10 or len(data["short"])!=10:
            raise ValueError("ARBAT must have ten explicit slots including null")
        for i,case_id in enumerate(case_names):
            row=lookup.get((section,case_id))
            if row is None:
                raise ValueError("Missing CScore case "+section+"/"+case_id)
            kl=data["long"][i]
            ks=data["short"][i]
            s=row.get("cscore_status")
            primary=parse_num(row.get("preliminary_utilization"))
            primary_short=parse_num(row.get("preliminary_utilization_short"))
            native=parse_num(row.get("native_utilization"))
            native_short=parse_num(row.get("native_utilization_short"))
            reported=kl is not None and ks is not None
            if (kl is None)!=(ks is None):
                raise ValueError("Inconsistent short/long ARBAT report")
            if reported and (kl<0 or ks<0):
                raise ValueError("Bad ARBAT crack ratio")
            if not reported:
                quality="ARBAT_NOT_REPORTED"
            elif s!="research_calculated" or None in (primary,primary_short,native,native_short):
                quality="CSCORE_INDETERMINATE"
            elif i==9:
                quality="N_LONG_NOT_MATCHED_T10"
            elif section in GEOM_PENDING:
                quality="GEOMETRY_UNVERIFIED"
            else:
                quality="PRELIMINARILY_COMPARABLE"
            if i==8 and reported:
                total=parse_num(row.get("Mx_total_kNm"))
                long_=parse_num(row.get("Mx_long_kNm"))
                if total is None or long_ is None or abs(long_/total-0.5)>1e-7:
                    raise ValueError("T09 sustained bending changed unexpectedly")
            rec={
                "section_id":section,"case_id":case_id,"class":quality,
                "arbat_long_K":kl,"arbat_short_K":ks,
                "primary_long_K":primary,"primary_short_K":primary_short,
                "native_long_K":native,"native_short_K":native_short,
                "arbat_long_width_mm":kl*ref["limits"]["long_mm"] if reported else None,
                "arbat_short_width_mm":ks*ref["limits"]["short_mm"] if reported else None,
                "long_error_abs_percent":100*abs(primary/kl-1)
                    if reported and primary is not None and kl>0 else None,
                "short_error_abs_percent":100*abs(primary_short/ks-1)
                    if reported and primary_short is not None and ks>0 else None,
                "preliminary_only_not_design_approved":True,
            }
            records.append(rec)
    included=[x for x in records if x["class"]=="PRELIMINARILY_COMPARABLE"]
    def avg_error(key):
        data=[x[key] for x in included if x[key] is not None]
        return sum(data)/len(data) if data else None
    counts={category:sum(x["class"]==category for x in records)
            for category in sorted({x["class"] for x in records})}
    metrics={
        "source":"Independent ARBAT 21.1.9.3 RTF/SAV versus CScore experimental",
        "coverage":{"total":len(records),"arbat_printed":sum(x["arbat_long_K"] is not None for x in records),
                    "cscore_numeric":sum(r.get("cscore_status")=="research_calculated" for r in rows),
                    "preliminarily_comparable":len(included),"classes":counts},
        "comparable_long_mape_percent":avg_error("long_error_abs_percent"),
        "comparable_short_mape_percent":avg_error("short_error_abs_percent"),
        "methodology":"Percent deviations are only research diagnostics; geometry axes must be independently verified. Native method remains unchanged.",
        "not_design_approved":True,
    }
    return records,metrics


def run(inp:Path,out:Path):
    ref=json.loads(REF.read_text(encoding="utf-8"))
    with inp.open(encoding="utf-8-sig",newline="") as handle:
        source=list(csv.DictReader(handle,delimiter=";"))
    records,metrics=compare(ref,source)
    out.mkdir(parents=True,exist_ok=True)
    with (out/"arbat70_aligned_comparison.csv").open("w",encoding="utf-8-sig",newline="") as handle:
        writer=csv.DictWriter(handle,fieldnames=list(records[0]),delimiter=";")
        writer.writeheader();writer.writerows(records)
    (out/"arbat70_aligned_metrics.json").write_text(
        json.dumps(metrics,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print("ARBAT limited crack-width: printed",metrics["coverage"]["arbat_printed"],
          "of 70; preliminarily comparable",metrics["coverage"]["preliminarily_comparable"])
    print("Long-term MAPE",metrics["comparable_long_mape_percent"],
          "%; short-term",metrics["comparable_short_mape_percent"],"%")
    print("All unmatched and indeterminate rows remain NOT VERIFIED; no final design approval.")
    return records,metrics


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--csv",type=Path,required=True)
    parser.add_argument("--out",type=Path,required=True)
    a=parser.parse_args()
    run(a.csv,a.out)
